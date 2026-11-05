namespace AutoFixScout;

public interface IProbes
{
    Task<(int ExitCode, string Output)> Execute(string command);
}

public sealed class ProbesReal : IProbes
{
    public async Task<(int ExitCode, string Output)> Execute(string command)
    {
        // MVP stub - real probes would be implemented here
        await Task.Delay(100);
        return (0, "Probe executed");
    }
}
