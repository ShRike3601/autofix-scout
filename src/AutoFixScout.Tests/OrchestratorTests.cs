namespace AutoFixScout.Tests;

using Xunit;
using Moq;

/// <summary>
/// Unit tests for orchestrator + session log (section 16).
/// </summary>
public sealed class OrchestratorTests
{
    [Fact]
    public async Task CreateSession_ShouldReturnValidSessionId()
    {
        var mockProbe = new Mock<IProbe>();
        var orchestrator = new Orchestrator(mockProbe.Object);

        var session = await orchestrator.CreateSessionAsync("audio", "1.0.0", "base");

        Assert.NotNull(session);
        Assert.Equal(Session.Symptom.Audio, session.Symptom);
    }

    [Fact]
    public async Task TriageAudio_ShouldIdentifyMute()
    {
        var mockProbe = new Mock<IProbe>();
        mockProbe.Setup(p => p.ProbeAudioStateAsync()).Returns(Task.FromResult(new AudioState
        {
            DefaultOutputDevice = "Speakers", Muted = true, Volume = 100,
            EndpointType = "Speakers", ActiveEndpoints = new List<string> { "Speakers" }
        }));

        var orchestrator = new Orchestrator(mockProbe.Object);
        var triaged = await orchestrator.TriageAudioAsync();

        Assert.Contains(triaged.Findings, f => f.Title == "Muted");
    }

    [Fact]
    public async Task TriageNetwork_ShouldIdentifyAirplaneMode()
    {
        var mockProbe = new Mock<IProbe>();
        mockProbe.Setup(p => p.ProbeNetworkAsync()).Returns(Task.FromResult(new NetworkState
        {
            AdapterName = "Intel(R) Wi‑Fi 6 AX201", AirplaneMode = true, Enabled = false,
            GatewayReachable = false, DnsResolving = true
        }));

        var orchestrator = new Orchestrator(mockProbe.Object);
        var triaged = await orchestrator.TriageNetworkAsync();

        Assert.Contains(triaged.Findings, f => f.Title == "Airplane mode");
    }
}

/// <summary>
/// Tests for session log CRUD operations.
/// </summary>
public sealed class SessionManagerTests
{
    private readonly SessionManager _manager = new("test_session.db");

    [Fact]
    public async Task Initialize_ShouldCreateTablesAndDefaultSettings()
    {
        await _manager.InitializeAsync();
        var settings = await _manager.GetSettingsAsync();

        Assert.Equal(3, settings.Count());  // telemetry_opt_in, llm_enabled, helper_approval_required_for_t2
    }

    [Fact]
    public async Task CreateAndCompleteStep_ShouldLogSuccess()
    {
        await _manager.InitializeAsync();

        var step = await _manager.CreateStepAsync("sess1", "step_probe_audio_state", "probe.audio_state", 0, "playbook");
        Assert.NotNull(step);

        await _manager.CompleteStepAsync(step!.StepId, StepLog.StepResult.Success);

        // Verify in database (via query)
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=test_session.db");
        await conn.OpenAsync();

        string sql = "SELECT COUNT(*) FROM step_log WHERE session_id = 'sess1' AND result = 'success'";
        int count = (int)conn.ExecuteScalarAsync(sql).Result;
        Assert.Equal(1, count);
    }
}
