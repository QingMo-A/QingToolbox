using StartupReliabilitySmokeTest.Tests;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Task paths", TaskPathTests.RunAsync),
    ("Task Scheduler interop", SchedulerInteropTests.RunAsync),
    ("Definitions and presence", DefinitionTests.RunAsync),
    ("Transactions", TransactionTests.RunAsync),
    ("Reconcile", ReconcileTests.RunAsync),
    ("Journal", JournalTests.RunAsync),
    ("Correlated startup test", StartupTestTests.RunAsync),
    ("Pipeline", PipelineTests.RunAsync),
    ("Notification", NotificationTests.RunAsync),
    ("Installer", InstallerContractTests.RunAsync)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
Console.WriteLine($"Startup reliability smoke test passed ({tests.Length} suites).");
