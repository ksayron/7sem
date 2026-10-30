'use strict';

const grpc = require('@grpc/grpc-js');
const protoLoader = require('@grpc/proto-loader');
const path = require('node:path');

const args = process.argv.slice(2);
if (args.length !== 0 && (args.length !== 2 || args[0] !== '--port')) {
  console.error('Usage: node server.js [--port 8302]');
  process.exit(2);
}
const port = args.length === 0 ? 8302 : Number(args[1]);
if (!Number.isInteger(port) || port < 1 || port > 65535) {
  console.error('Port must be between 1 and 65535.');
  process.exit(2);
}

const definition = protoLoader.loadSync(path.join(__dirname, '../protos/calculator.proto'));
const { Calculator } = grpc.loadPackageDefinition(definition).calculator.v1;

function rpcError(code, details) {
  return Object.assign(new Error(details), { code, details });
}

function binary(operation) {
  return (call, callback) => {
    // Absent proto3 numeric fields have the default value 0.
    const { x = 0, y = 0 } = call.request;
    if (!Number.isFinite(x) || !Number.isFinite(y)) {
      callback(rpcError(grpc.status.INVALID_ARGUMENT, 'Operands must be finite doubles.'));
      return;
    }
    try {
      const result = operation(x, y);
      if (!Number.isFinite(result)) {
        throw rpcError(grpc.status.OUT_OF_RANGE, 'Result exceeds finite double range.');
      }
      callback(null, { result });
    } catch (error) {
      callback(error);
    }
  };
}

const server = new grpc.Server();
server.addService(Calculator.service, {
  SUM: binary((x, y) => x + y),
  SUB: binary((x, y) => x - y),
  MUL: binary((x, y) => x * y),
  DIV: binary((x, y) => {
    if (y === 0) throw rpcError(grpc.status.INVALID_ARGUMENT, 'Division by zero.');
    return x / y;
  }),
  FACT(call, callback) {
    const { x = 0 } = call.request;
    if (x < 0) {
      callback(rpcError(grpc.status.INVALID_ARGUMENT, 'Factorial requires x >= 0.'));
      return;
    }
    if (x > 12) {
      callback(rpcError(grpc.status.OUT_OF_RANGE, 'Factorial exceeds Int32.MaxValue.'));
      return;
    }
    let result = 1;
    for (let i = 2; i <= x; i++) result *= i;
    callback(null, { result });
  },
});

const address = `127.0.0.1:${port}`;
server.bindAsync(address, grpc.ServerCredentials.createInsecure(), (error) => {
  if (error) {
    console.error(error.message);
    process.exitCode = 1;
    return;
  }
  console.log(`gRPCServer02 listening on ${address} (HTTP/2, plaintext)`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    const timer = setTimeout(() => server.forceShutdown(), 3000);
    timer.unref();
    server.tryShutdown(() => clearTimeout(timer));
  });
}
