using QingToolbox.Shell.Services;

if (args.Length != 1 || !args[0].Equals("--remove-owned-startup", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: QingToolbox.StartupMaintenance --remove-owned-startup");
    return 2;
}

var failed = false;
try
{
    var scheduler = new WindowsTaskSchedulerStore();
    scheduler.Delete();
    scheduler.DeleteOwnedStartupTests();
}
catch (Exception exception)
{
    failed = true;
    Console.Error.WriteLine($"Task Scheduler cleanup failed: {exception.GetType().Name}");
}
try { new WindowsRunRegistrationStore().Delete(); }
catch (Exception exception)
{
    failed = true;
    Console.Error.WriteLine($"Registry Run cleanup failed: {exception.GetType().Name}");
}

return failed ? 1 : 0;
