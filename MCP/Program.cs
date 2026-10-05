using JevMcp;
using MCPSharp;

// stdio MCP server. stdout carries the protocol stream; diagnostics go to stderr.
// Credentials come from appsettings.json ("TypeSafe" section) or env vars (TYPESAFE__ApiKey).
MCPServer.Register<JevMcpTools>();
await MCPServer.StartAsync("jev-eval", "1.0.0");
