namespace NugetManager.Sdk;

internal sealed partial class SdkClient
{
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string?>? _environment;

    public SdkClient(IProcessRunner runner, IReadOnlyDictionary<string, string?>? environment = null)
    {
        _runner = runner;
        _environment = environment;
    }
}
