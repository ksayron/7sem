package main

import (
	"context"
	"flag"
	"fmt"
	"math"
	"os"
	"time"

	pb "example.com/pws/lab2-grpc/gRPCClient02/calculatorpb"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/status"
)

func main() {
	os.Exit(run())
}

func run() int {
	address := flag.String("address", "127.0.0.1:8301", "gRPC server host:port (plaintext HTTP/2)")
	flag.Parse()
	if flag.NArg() != 0 {
		flag.Usage()
		return 2
	}
	conn, err := grpc.NewClient(*address, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 1
	}
	defer conn.Close()
	client := pb.NewCalculatorClient(conn)
	fmt.Printf("gRPCClient02 (Go) -> %s\n", *address)

	type testCase struct {
		label, method string
		x, y, want    float64
		code          codes.Code
		detail        string
	}
	cases := []testCase{
		{label: "SUM(10.5, 2.5)", method: "SUM", x: 10.5, y: 2.5, want: 13},
		{label: "SUB(10, 3)", method: "SUB", x: 10, y: 3, want: 7},
		{label: "MUL(2.5, 4)", method: "MUL", x: 2.5, y: 4, want: 10},
		{label: "DIV(7, 2)", method: "DIV", x: 7, y: 2, want: 3.5},
		{label: "SUM(-5, 2)", method: "SUM", x: -5, y: 2, want: -3},
		{label: "SUB(2, 5)", method: "SUB", x: 2, y: 5, want: -3},
		{label: "MUL(0, 4)", method: "MUL", y: 4},
		{label: "DIV(0, 2)", method: "DIV", y: 2},
		{label: "DIV(7, -2)", method: "DIV", x: 7, y: -2, want: -3.5},
		{label: "FACT(5)", method: "FACT", x: 5, want: 120},
		{label: "FACT(0)", method: "FACT", want: 1},
		{label: "FACT(1)", method: "FACT", x: 1, want: 1},
		{label: "FACT(12)", method: "FACT", x: 12, want: 479001600},
		{label: "DIV(1, 0)", method: "DIV", x: 1, code: codes.InvalidArgument, detail: "Division by zero."},
		{label: "DIV(0, -0)", method: "DIV", y: math.Copysign(0, -1), code: codes.InvalidArgument, detail: "Division by zero."},
		{label: "FACT(13)", method: "FACT", x: 13, code: codes.OutOfRange, detail: "Factorial exceeds Int32.MaxValue."},
		{label: "FACT(Int32.MaxValue)", method: "FACT", x: math.MaxInt32, code: codes.OutOfRange, detail: "Factorial exceeds Int32.MaxValue."},
		{label: "FACT(-1)", method: "FACT", x: -1, code: codes.InvalidArgument, detail: "Factorial requires x >= 0."},
		{label: "FACT(Int32.MinValue)", method: "FACT", x: math.MinInt32, code: codes.InvalidArgument, detail: "Factorial requires x >= 0."},
		{label: "SUM(NaN, 1)", method: "SUM", x: math.NaN(), y: 1, code: codes.InvalidArgument, detail: "Operands must be finite doubles."},
		{label: "MUL(+Infinity, 2)", method: "MUL", x: math.Inf(1), y: 2, code: codes.InvalidArgument, detail: "Operands must be finite doubles."},
		{label: "MUL(Double.MaxValue, 2)", method: "MUL", x: math.MaxFloat64, y: 2, code: codes.OutOfRange, detail: "Result exceeds finite double range."},
		{label: "SUM(1, 2) after errors", method: "SUM", x: 1, y: 2, want: 3},
	}
	failures := 0
	for _, tc := range cases {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		actual, callErr := invoke(ctx, client, tc.method, tc.x, tc.y)
		cancel()
		passed := false
		var result string
		if callErr != nil {
			s := status.Convert(callErr)
			passed = tc.code != codes.OK && s.Code() == tc.code && s.Message() == tc.detail
			result = fmt.Sprintf("handled gRPC error %s (%d): %s", s.Code(), s.Code(), s.Message())
		} else {
			passed = tc.code == codes.OK && actual == tc.want
			result = fmt.Sprintf("= %.17g", actual)
		}
		mark := "PASS"
		if !passed {
			failures++
			mark = "FAIL"
		}
		fmt.Printf("[%s] %s %s\n", mark, tc.label, result)
	}
	fmt.Printf("Checks: %d/%d passed, %d failed.\n", len(cases)-failures, len(cases), failures)
	if failures != 0 {
		return 1
	}
	return 0
}

func invoke(ctx context.Context, client pb.CalculatorClient, method string, x, y float64) (float64, error) {
	options := []grpc.CallOption{grpc.WaitForReady(true)}
	if method == "FACT" {
		reply, err := client.FACT(ctx, &pb.FactorialRequest{X: int32(x)}, options...)
		if err != nil {
			return 0, err
		}
		return float64(reply.Result), nil
	}
	request := &pb.BinaryRequest{X: x, Y: y}
	var reply *pb.DoubleReply
	var err error
	switch method {
	case "SUM":
		reply, err = client.SUM(ctx, request, options...)
	case "SUB":
		reply, err = client.SUB(ctx, request, options...)
	case "MUL":
		reply, err = client.MUL(ctx, request, options...)
	case "DIV":
		reply, err = client.DIV(ctx, request, options...)
	default:
		return 0, fmt.Errorf("unknown method: %s", method)
	}
	if err != nil {
		return 0, err
	}
	return reply.Result, nil
}
