using System.Text.Json;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;
using MeetNow.Recording.Core.Recording;
using Xunit;

namespace MeetNow.Recording.Core.Tests.Recording;

public class SessionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RecorderConfig _config;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MeetNowTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _config = new RecorderConfig { OutputDir = _tempDir, SessionGapMinutes = 10 };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void StartNewSession_CreatesDirectoryAndSessionJson()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("TestLoopback", "TestMic");

        Assert.True(Directory.Exists(session.SessionDir));
        Assert.True(Directory.Exists(Path.Combine(session.SessionDir, "chunks")));
        Assert.True(Directory.Exists(Path.Combine(session.SessionDir, "transcripts")));
        Assert.True(File.Exists(Path.Combine(session.SessionDir, "session.json")));
    }

    [Fact]
    public void StartNewSession_WritesCorrectMetadata()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("Speakers (Test)", "Mic (Test)");

        var json = File.ReadAllText(Path.Combine(session.SessionDir, "session.json"));
        var meta = JsonSerializer.Deserialize<SessionMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(SessionStatus.Recording, meta.Status);
        Assert.Equal("Speakers (Test)", meta.CaptureDevices.Loopback);
        Assert.Equal("Mic (Test)", meta.CaptureDevices.Microphone);
        Assert.Equal(0, meta.ChunkCount);
        Assert.Null(meta.EndTimeUtc);
    }

    [Fact]
    public void SessionId_MatchesFolderName()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("lb", "mic");
        var folderName = Path.GetFileName(session.SessionDir);
        Assert.Equal(folderName, session.SessionId);
    }

    [Fact]
    public void NextChunkIndex_Increments()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("lb", "mic");

        Assert.Equal(1, session.NextChunkIndex());
        Assert.Equal(2, session.NextChunkIndex());
        Assert.Equal(3, session.NextChunkIndex());
    }

    [Fact]
    public void CompleteSession_UpdatesMetadata()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("lb", "mic");
        session.NextChunkIndex();
        session.NextChunkIndex();
        session.Complete();

        var json = File.ReadAllText(Path.Combine(session.SessionDir, "session.json"));
        var meta = JsonSerializer.Deserialize<SessionMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(SessionStatus.Completed, meta.Status);
        Assert.NotNull(meta.EndTimeUtc);
        Assert.Equal(2, meta.ChunkCount);
    }

    [Fact]
    public void ShouldStartNewSession_TrueWhenNoActiveSession()
    {
        var manager = new SessionManager(_config);
        Assert.True(manager.ShouldStartNewSession(lastChunkTimeUtc: null));
    }

    [Fact]
    public void ShouldStartNewSession_TrueWhenGapExceedsThreshold()
    {
        var manager = new SessionManager(_config);
        var lastChunkTime = DateTime.UtcNow.AddMinutes(-11);
        Assert.True(manager.ShouldStartNewSession(lastChunkTime));
    }

    [Fact]
    public void ShouldStartNewSession_FalseWhenWithinGap()
    {
        var manager = new SessionManager(_config);
        var lastChunkTime = DateTime.UtcNow.AddMinutes(-5);
        Assert.False(manager.ShouldStartNewSession(lastChunkTime));
    }
}
