using Receivify.Tests;

Console.WriteLine("Receivify .NET SDK test suite (offline — no socket is opened)");

await ClientTests.RunAsync().ConfigureAwait(false);
await WebhookTests.RunAsync().ConfigureAwait(false);
await MoneyTests.RunAsync().ConfigureAwait(false);

return Test.Summarise();
