package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"math"
	"net/http"
	"os"
	"strconv"
	"strings"
	"testing"
)

func testClient(t *testing.T) *rpcClient {
	t.Helper()
	url := os.Getenv("JSONRPC_URL")
	if url == "" {
		t.Skip("set JSONRPC_URL to a running server, or use ../Test-Lab.ps1")
	}
	return newClient(url)
}

func send(t *testing.T, client *rpcClient, request string, status int) []byte {
	t.Helper()
	actual, body, err := client.send(request)
	if err != nil {
		t.Fatal(err)
	}
	if actual != status {
		t.Fatalf("HTTP %d, want %d: %s", actual, status, body)
	}
	return body
}

func decode(t *testing.T, body []byte) rpcResponse {
	t.Helper()
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(body, &fields); err != nil {
		t.Fatalf("invalid response %s: %v", body, err)
	}
	_, result := fields["result"]
	_, rpcErr := fields["error"]
	if result == rpcErr || fields["id"] == nil || string(fields["jsonrpc"]) != `"2.0"` {
		t.Fatalf("invalid JSON-RPC envelope: %s", body)
	}
	var response rpcResponse
	if err := json.Unmarshal(body, &response); err != nil {
		t.Fatal(err)
	}
	if rpcErr && (response.Error == nil || response.Error.Message == "") {
		t.Fatalf("invalid error: %s", body)
	}
	return response
}

func wantResult(t *testing.T, response rpcResponse, expected float64) {
	t.Helper()
	if response.Error != nil {
		t.Fatalf("unexpected error: %+v", response.Error)
	}
	actual, err := strconv.ParseFloat(string(response.Result), 64)
	if err != nil || math.Abs(actual-expected) > 1e-12 {
		t.Fatalf("result %s, want %v", response.Result, expected)
	}
}

func TestArithmetic(t *testing.T) {
	client := testClient(t)
	tests := []struct {
		method, positional, named string
		expected                  float64
	}{
		{"SUM", `[10.5,2.5]`, `{"y":2.5,"x":10.5}`, 13},
		{"SUB", `[10,3]`, `{"y":3,"x":10}`, 7},
		{"MUL", `[2.5,4]`, `{"y":4,"x":2.5}`, 10},
		{"DIV", `[7,2]`, `{"y":2,"x":7}`, 3.5},
		{"FACT", `[5]`, `{"x":5}`, 120},
		{"SUM", `[-2,0.5]`, `{"x":-2,"y":0.5}`, -1.5},
		{"SUB", `[3,10]`, `{"x":3,"y":10}`, -7},
		{"MUL", `[-2,0]`, `{"x":-2,"y":0}`, 0},
		{"DIV", `[7,-2]`, `{"x":7,"y":-2}`, -3.5},
		{"FACT", `[0]`, `{"x":0}`, 1},
		{"FACT", `[1]`, `{"x":1}`, 1},
		{"FACT", `[12]`, `{"x":12}`, 479001600},
	}
	for i, test := range tests {
		for style, params := range map[string]string{"positional": test.positional, "named": test.named} {
			t.Run(fmt.Sprintf("%s_%d_%s", test.method, i, style), func(t *testing.T) {
				request := fmt.Sprintf(`{"jsonrpc":"2.0","method":%q,"params":%s,"id":42}`, test.method, params)
				response := decode(t, send(t, client, request, http.StatusOK))
				if string(response.ID) != "42" {
					t.Fatalf("id: %s", response.ID)
				}
				wantResult(t, response, test.expected)
			})
		}
	}
}

func TestErrors(t *testing.T) {
	client := testClient(t)
	tests := []struct {
		name, request string
		code          int
		id            string
	}{
		{"division_zero", `{"jsonrpc":"2.0","method":"DIV","params":[1,0],"id":1}`, -32001, "1"},
		{"division_negative_zero", `{"jsonrpc":"2.0","method":"DIV","params":{"x":1,"y":-0.0},"id":1}`, -32001, "1"},
		{"factorial_overflow", `{"jsonrpc":"2.0","method":"FACT","params":[13],"id":1}`, -32002, "1"},
		{"factorial_large", `{"jsonrpc":"2.0","method":"FACT","params":{"x":2147483647},"id":1}`, -32002, "1"},
		{"factorial_negative", `{"jsonrpc":"2.0","method":"FACT","params":[-1],"id":1}`, -32602, "1"},
		{"factorial_fraction", `{"jsonrpc":"2.0","method":"FACT","params":[2.5],"id":1}`, -32602, "1"},
		{"factorial_outside_int", `{"jsonrpc":"2.0","method":"FACT","params":[2147483648],"id":1}`, -32602, "1"},
		{"factorial_string", `{"jsonrpc":"2.0","method":"FACT","params":["5"],"id":1}`, -32602, "1"},
		{"method_missing", `{"jsonrpc":"2.0","method":"UNKNOWN","params":[],"id":"missing"}`, -32601, `"missing"`},
		{"method_case", `{"jsonrpc":"2.0","method":"sum","params":[1,2],"id":1}`, -32601, "1"},
		{"missing_params", `{"jsonrpc":"2.0","method":"SUM","id":1}`, -32602, "1"},
		{"too_few", `{"jsonrpc":"2.0","method":"SUM","params":[1],"id":1}`, -32602, "1"},
		{"too_many", `{"jsonrpc":"2.0","method":"SUM","params":[1,2,3],"id":1}`, -32602, "1"},
		{"named_missing", `{"jsonrpc":"2.0","method":"SUM","params":{"x":1},"id":1}`, -32602, "1"},
		{"named_extra", `{"jsonrpc":"2.0","method":"SUM","params":{"x":1,"y":2,"z":3},"id":1}`, -32602, "1"},
		{"named_case", `{"jsonrpc":"2.0","method":"SUM","params":{"X":1,"y":2},"id":1}`, -32602, "1"},
		{"named_duplicate", `{"jsonrpc":"2.0","method":"SUM","params":{"x":1,"x":2},"id":1}`, -32602, "1"},
		{"string_number", `{"jsonrpc":"2.0","method":"SUM","params":["1",2],"id":1}`, -32602, "1"},
		{"null_number", `{"jsonrpc":"2.0","method":"SUM","params":[null,2],"id":1}`, -32602, "1"},
		{"bool_number", `{"jsonrpc":"2.0","method":"SUM","params":[true,2],"id":1}`, -32602, "1"},
		{"scalar_params", `{"jsonrpc":"2.0","method":"SUM","params":42,"id":1}`, -32602, "1"},
		{"null_params", `{"jsonrpc":"2.0","method":"SUM","params":null,"id":1}`, -32602, "1"},
		{"infinite_input", `{"jsonrpc":"2.0","method":"SUM","params":[1e400,1],"id":1}`, -32602, "1"},
		{"double_overflow", `{"jsonrpc":"2.0","method":"MUL","params":[1e308,1e308],"id":1}`, -32003, "1"},
		{"malformed_json", `{"jsonrpc":`, -32700, "null"},
		{"empty_body", ``, -32700, "null"},
		{"trailing_json", `{} {}`, -32700, "null"},
		{"json_null", `null`, -32600, "null"},
		{"json_number", `42`, -32600, "null"},
		{"json_string", `"hello"`, -32600, "null"},
		{"empty_object", `{}`, -32600, "null"},
		{"empty_batch", `[]`, -32600, "null"},
		{"version", `{"jsonrpc":"1.0","method":"SUM","id":1}`, -32600, "null"},
		{"numeric_version", `{"jsonrpc":2,"method":"SUM","id":1}`, -32600, "null"},
		{"method_type", `{"jsonrpc":"2.0","method":1,"id":1}`, -32600, "null"},
		{"bool_id", `{"jsonrpc":"2.0","method":"SUM","params":[1,2],"id":true}`, -32600, "null"},
		{"object_id", `{"jsonrpc":"2.0","method":"SUM","params":[1,2],"id":{}}`, -32600, "null"},
		{"duplicate_member", `{"jsonrpc":"2.0","method":"SUM","id":1,"id":2}`, -32600, "null"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			response := decode(t, send(t, client, test.request, http.StatusOK))
			if response.Error == nil || response.Error.Code != test.code || string(response.ID) != test.id {
				t.Fatalf("got %+v id=%s, want error %d id=%s", response, response.ID, test.code, test.id)
			}
		})
	}
}

func TestIDs(t *testing.T) {
	client := testClient(t)
	for _, id := range []string{`"abc"`, `""`, `null`, `0`, `-7`, `1.5`, `9007199254740993`} {
		t.Run(id, func(t *testing.T) {
			request := `{"jsonrpc":"2.0","method":"SUM","params":[1,2],"id":` + id + `}`
			response := decode(t, send(t, client, request, http.StatusOK))
			if string(response.ID) != id {
				t.Fatalf("id changed: %s => %s", id, response.ID)
			}
			wantResult(t, response, 3)
		})
	}
}

func TestNotifications(t *testing.T) {
	client := testClient(t)
	tests := []string{
		`{"jsonrpc":"2.0","method":"SUM","params":[1,2]}`,
		`{"jsonrpc":"2.0","method":"DIV","params":[1,0]}`,
		`{"jsonrpc":"2.0","method":"FACT","params":[13]}`,
		`{"jsonrpc":"2.0","method":"MISSING"}`,
		`{"jsonrpc":"2.0","method":"SUM","params":[1]}`,
		`[{"jsonrpc":"2.0","method":"FACT","params":[5]}]`,
		`[{"jsonrpc":"2.0","method":"SUM","params":[1,2]},{"jsonrpc":"2.0","method":"DIV","params":[1,0]}]`,
	}
	for i, request := range tests {
		t.Run(fmt.Sprint(i), func(t *testing.T) {
			if body := send(t, client, request, http.StatusNoContent); len(body) != 0 {
				t.Fatalf("notification returned a body: %s", body)
			}
		})
	}
}

func TestMixedBatch(t *testing.T) {
	client := testClient(t)
	body := send(t, client, `[
  {"jsonrpc":"2.0","method":"SUM","params":[2,3],"id":"sum"},
  {"jsonrpc":"2.0","method":"DIV","params":{"x":1,"y":0},"id":"div-error"},
  {"jsonrpc":"2.0","method":"SUM","params":[1,2]},
  {"jsonrpc":"2.0","method":"FACT","params":[13],"id":"overflow"},
  17,
  {"jsonrpc":"2.0","method":"FACT","params":{"x":5},"id":"fact"},
  {"jsonrpc":"2.0","method":"NOPE","id":"unknown"},
  {"jsonrpc":"2.0","method":"SUB","params":[1],"id":"params-error"}
]`, http.StatusOK)
	var items []json.RawMessage
	if err := json.Unmarshal(body, &items); err != nil || len(items) != 7 {
		t.Fatalf("expected seven responses: %s (%v)", body, err)
	}
	// Match by id; JSON-RPC does not promise response ordering.
	expected := map[string]int{`"sum"`: 0, `"div-error"`: -32001, `"overflow"`: -32002,
		`null`: -32600, `"fact"`: 0, `"unknown"`: -32601, `"params-error"`: -32602}
	for _, item := range items {
		response := decode(t, item)
		id := string(response.ID)
		code, exists := expected[id]
		if !exists {
			t.Fatalf("unexpected or duplicate id: %s", id)
		}
		delete(expected, id)
		if code != 0 {
			if response.Error == nil || response.Error.Code != code {
				t.Fatalf("id %s: expected error %d: %s", id, code, item)
			}
		} else if id == `"sum"` {
			wantResult(t, response, 5)
		} else {
			wantResult(t, response, 120)
		}
	}
}

func TestBatchShapes(t *testing.T) {
	client := testClient(t)
	for _, request := range []string{`[1]`, `[1,true,null,[],{}]`, `[{"jsonrpc":"2.0","method":"SUM","params":[1,2],"id":1}]`} {
		t.Run(request, func(t *testing.T) {
			body := send(t, client, request, http.StatusOK)
			if !bytes.HasPrefix(bytes.TrimSpace(body), []byte("[")) {
				t.Fatalf("non-empty batch must return an array: %s", body)
			}
			var items []json.RawMessage
			if err := json.Unmarshal(body, &items); err != nil {
				t.Fatal(err)
			}
			if strings.Contains(request, "SUM") {
				if len(items) != 1 {
					t.Fatal(string(body))
				}
				wantResult(t, decode(t, items[0]), 3)
			} else {
				var input []json.RawMessage
				_ = json.Unmarshal([]byte(request), &input)
				if len(items) != len(input) {
					t.Fatalf("lost invalid batch items: %s", body)
				}
				for _, item := range items {
					response := decode(t, item)
					if response.Error == nil || response.Error.Code != -32600 || string(response.ID) != "null" {
						t.Fatalf("expected Invalid Request: %s", item)
					}
				}
			}
		})
	}
}
