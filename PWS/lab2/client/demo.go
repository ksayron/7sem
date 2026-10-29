package main

import "fmt"

func runDemo(client *rpcClient) error {
	cases := []struct{ name, request string }{
		{"SUM positional: 10.5 + 2.5 = 13", `{"jsonrpc":"2.0","method":"SUM","params":[10.5,2.5],"id":1}`},
		{"SUM named: 10.5 + 2.5 = 13", `{"jsonrpc":"2.0","method":"SUM","params":{"y":2.5,"x":10.5},"id":2}`},
		{"SUB positional: 10 - 3 = 7", `{"jsonrpc":"2.0","method":"SUB","params":[10,3],"id":3}`},
		{"SUB named: 10 - 3 = 7", `{"jsonrpc":"2.0","method":"SUB","params":{"y":3,"x":10},"id":4}`},
		{"MUL positional: 2.5 * 4 = 10", `{"jsonrpc":"2.0","method":"MUL","params":[2.5,4],"id":5}`},
		{"MUL named: 2.5 * 4 = 10", `{"jsonrpc":"2.0","method":"MUL","params":{"x":2.5,"y":4},"id":6}`},
		{"DIV positional: 7 / 2 = 3.5", `{"jsonrpc":"2.0","method":"DIV","params":[7,2],"id":7}`},
		{"DIV named: 7 / 2 = 3.5", `{"jsonrpc":"2.0","method":"DIV","params":{"y":2,"x":7},"id":8}`},
		{"FACT positional: 5! = 120", `{"jsonrpc":"2.0","method":"FACT","params":[5],"id":9}`},
		{"FACT named: 12! = 479001600", `{"jsonrpc":"2.0","method":"FACT","params":{"x":12},"id":10}`},
		{"Division by zero: error -32001", `{"jsonrpc":"2.0","method":"DIV","params":[1,0],"id":"division-error"}`},
		{"Factorial overflow: error -32002", `{"jsonrpc":"2.0","method":"FACT","params":{"x":13},"id":"overflow"}`},
		{"Mixed batch: results, errors, notification (no response for it)", `[
  {"jsonrpc":"2.0","method":"SUM","params":[2,3],"id":"batch-sum"},
  {"jsonrpc":"2.0","method":"SUB","params":{"y":3,"x":10},"id":"batch-sub"},
  {"jsonrpc":"2.0","method":"MUL","params":[3,4],"id":"batch-mul"},
  {"jsonrpc":"2.0","method":"DIV","params":{"x":9,"y":2},"id":"batch-div"},
  {"jsonrpc":"2.0","method":"FACT","params":[0],"id":"batch-fact"},
  {"jsonrpc":"2.0","method":"DIV","params":[1,0],"id":"batch-error"},
  {"jsonrpc":"2.0","method":"FACT","params":[13],"id":"batch-overflow"},
  {"jsonrpc":"2.0","method":"SUM","params":[1,2]}
]`},
		{"Notification", `{"jsonrpc":"2.0","method":"SUM","params":[1,2]}`},
		{"Parse error: -32700", `{"jsonrpc":`},
	}
	for _, test := range cases {
		fmt.Printf("\n=== %s ===\n", test.name)
		if err := exchange(client, test.request); err != nil {
			return err
		}
	}
	return nil
}
