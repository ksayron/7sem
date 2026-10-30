using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("port", 8301);

builder.WebHost.ConfigureKestrel(options =>
    options.Listen(IPAddress.Loopback, port, endpoint => endpoint.Protocols = HttpProtocols.Http2));
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<gRPCServer01.CalculatorService>();
app.Run();
