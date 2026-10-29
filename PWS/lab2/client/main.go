package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"time"
)

type rpcResponse struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id"`
	Result  json.RawMessage `json:"result"`
	Error   *rpcError       `json:"error"`
}

type rpcError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

type rpcClient struct {
	url  string
	http *http.Client
}

func newClient(url string) *rpcClient {
	return &rpcClient{url: url, http: &http.Client{Timeout: 10 * time.Second}}
}

func (c *rpcClient) send(request string) (int, []byte, error) {
	response, err := c.http.Post(c.url, "application/json", bytes.NewBufferString(request))
	if err != nil {
		return 0, nil, err
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		return response.StatusCode, nil, err
	}
	if response.StatusCode != http.StatusOK && response.StatusCode != http.StatusNoContent {
		return response.StatusCode, body, fmt.Errorf("HTTP %d: %s", response.StatusCode, body)
	}
	return response.StatusCode, body, nil
}

func main() {
	url := flag.String("url", "http://127.0.0.1:8200/rpc", "JSON-RPC HTTP endpoint")
	request := flag.String("request", "", "send one raw JSON request or batch instead of the demo")
	file := flag.String("file", "", "read a raw JSON request or batch from a UTF-8 file")
	flag.Parse()
	if *file != "" {
		if *request != "" {
			fmt.Fprintln(os.Stderr, "use either -request or -file")
			os.Exit(1)
		}
		body, err := os.ReadFile(*file)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		*request = string(bytes.TrimPrefix(body, []byte{0xEF, 0xBB, 0xBF}))
		if *request == "" {
			fmt.Fprintln(os.Stderr, "request file is empty")
			os.Exit(1)
		}
	}
	client := newClient(*url)
	if *request != "" {
		if err := exchange(client, *request); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	if err := runDemo(client); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func exchange(client *rpcClient, request string) error {
	fmt.Printf("--> %s\n", request)
	status, body, err := client.send(request)
	if err != nil {
		return err
	}
	if status == http.StatusNoContent {
		fmt.Println("<-- HTTP 204 (notification: no JSON response)")
		return nil
	}
	var pretty bytes.Buffer
	if err := json.Indent(&pretty, body, "", "  "); err != nil {
		return fmt.Errorf("invalid JSON response: %w", err)
	}
	fmt.Printf("<-- HTTP %d\n%s\n", status, pretty.String())
	return nil
}
