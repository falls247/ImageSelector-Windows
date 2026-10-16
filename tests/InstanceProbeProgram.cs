using ImageSelector;

var coordinator = InstanceCoordinator.Enter();
if (coordinator is null)
{
    Console.WriteLine("DUPLICATE");
    return 7;
}
coordinator.MarkReady();
Console.WriteLine("OWNER_READY");
if (args.Contains("--hold"))
    Thread.Sleep(4000);
coordinator.MarkClosing();
coordinator.TraceExit();
// Exit without releasing the mutex before CLR teardown.
Environment.Exit(0);
return 0;
