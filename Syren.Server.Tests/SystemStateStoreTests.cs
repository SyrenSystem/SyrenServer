using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class SystemStateStoreTests
{
    [Fact]
    public void CreatesAndReloadsState()
    {
        using var directory = new TemporaryDirectory();
        var firstStore = CreateStore(directory.StatePath);
        firstStore.Save(new PersistentSystemState
        {
            StateId = firstStore.Current.StateId,
            Speakers =
            [
                new PersistentSpeakerState
                {
                    SpeakerId = "sensor",
                    Name = "Sensor",
                    SensorId = "sensor",
                    SnapClientId = "client",
                    Connected = false,
                    Volume = 42,
                },
            ],
        });

        var secondStore = CreateStore(directory.StatePath);

        Assert.Equal(firstStore.Current.StateId, secondStore.Current.StateId);
        Assert.Equal(42, secondStore.Current.Speakers.Single().Volume);
    }

    [Fact]
    public void CorruptStateStopsLoading()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, "not json");

        Assert.Throws<InvalidDataException>(() => CreateStore(directory.StatePath));
    }

    [Fact]
    public void NewStateSeedsBootstrapSpeakers()
    {
        using var directory = new TemporaryDirectory();
        var speakerOptions = new SpeakersOptions
        {
            SpeakersInfo = [Speaker("SENSOR", "PLAYER")],
        };

        var store = CreateStore(directory.StatePath, speakerOptions);

        PersistentSpeakerState speaker = Assert.Single(store.Current.Speakers);
        Assert.Equal("sensor", speaker.SensorId);
        Assert.Equal("player", speaker.SnapClientId);
        Assert.Equal(100, speaker.Volume);
        Assert.Equal(1, store.Current.Revision);
    }

    [Fact]
    public void ExistingStateIsNotReseeded()
    {
        using var directory = new TemporaryDirectory();
        var speakerOptions = new SpeakersOptions
        {
            SpeakersInfo = [Speaker("sensor", "player")],
        };
        var firstStore = CreateStore(directory.StatePath, speakerOptions);
        Assert.Single(firstStore.Current.Speakers);
        firstStore.Save(firstStore.Current with
        {
            Revision = firstStore.Current.Revision + 1,
            Speakers = [],
        });

        var secondStore = CreateStore(directory.StatePath, speakerOptions);

        Assert.Empty(secondStore.Current.Speakers);
        Assert.Equal(firstStore.Current.Revision, secondStore.Current.Revision);
    }

    [Fact]
    public void DuplicateSnapClientInOptionsDoesNotCrashOnLoad()
    {
        using var directory = new TemporaryDirectory();
        var speakerOptions = new SpeakersOptions
        {
            SpeakersInfo = [Speaker("sensor-one", "player"), Speaker("sensor-two", "PLAYER")],
        };

        var store = CreateStore(directory.StatePath, speakerOptions);

        PersistentSpeakerState speaker = Assert.Single(store.Current.Speakers);
        Assert.Equal("sensor-one", speaker.SensorId);
    }

    [Fact]
    public void VersionOneMigrationGroupsAllSpeakersWithConfiguredSources()
    {
        using var directory = new TemporaryDirectory();
        string stateId = Guid.NewGuid().ToString();
        File.WriteAllText(directory.StatePath, $$"""
            {
              "version": 1,
              "stateId": "{{stateId}}",
              "speakers": [
                { "sensorId": "AA", "connected": true, "volume": 50, "position": { "x": 1, "y": 2, "z": 3 } },
                { "sensorId": "BB", "connected": false, "volume": 20 }
              ]
            }
            """);
        var speakerOptions = new SpeakersOptions
        {
            SpeakersInfo = [Speaker("AA", "client-a"), Speaker("BB", "client-b"), Speaker("CC", "client-c")],
        };

        var store = CreateStore(directory.StatePath, speakerOptions);

        Assert.Equal(2, store.Current.Version);
        Assert.Equal(stateId, store.Current.StateId);
        Assert.Equal(1, store.Current.Revision);
        Assert.Equal(["aa", "bb", "cc"], store.Current.Speakers.Select(speaker => speaker.SpeakerId));
        PersistentPlaybackGroup group = Assert.Single(store.Current.Groups);
        Assert.Equal("default", group.Id);
        Assert.Equal(["aa", "bb", "cc"], group.SpeakerIds);
        Assert.Equal(["spotify", "laptop"], group.SourcePriority);
        Assert.Equal("manual", group.VolumeMode);
    }

    [Fact]
    public void UnknownGroupSourcesAreDroppedOnLoad()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, $$"""
            {
              "version": 2,
              "stateId": "{{Guid.NewGuid()}}",
              "revision": 3,
              "speakers": [
                {
                  "speakerId": "desk", "name": "Desk", "snapClientId": "desk-client",
                  "connected": false, "volume": 70
                }
              ],
              "groups": [
                {
                  "id": "office", "name": "Office", "speakerIds": ["desk"],
                  "sourcePriority": ["spotify", "radio"], "volumeMode": "manual", "masterVolume": 80
                }
              ]
            }
            """);

        var store = CreateStore(directory.StatePath);

        PersistentPlaybackGroup group = Assert.Single(store.Current.Groups);
        Assert.Equal(["spotify"], group.SourcePriority);
        Assert.Equal(4, store.Current.Revision);
        Assert.Equal(["spotify"], CreateStore(directory.StatePath).Current.Groups.Single().SourcePriority);
    }

    private static SystemStateStore CreateStore(string path, SpeakersOptions? speakerOptions = null) => new(
        Options.Create(new StateOptions { FilePath = path }),
        Options.Create(speakerOptions ?? new SpeakersOptions()),
        TestServices.CreatePlaybackOptions(),
        NullLogger<SystemStateStore>.Instance
    );

    private static SpeakerInfo Speaker(string sensorId, string snapClientId) => new()
    {
        SensorId = sensorId,
        SnapClientId = snapClientId,
        FullVolumeDistance = 500,
        MuteDistance = 3000,
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"syren-state-{Guid.NewGuid():N}"
        );

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_directory);
        }

        public string StatePath => Path.Combine(_directory, "state.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
