using System.Text.Json;
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
    public void ProfileMigrationKeepsCalibrationLevelsAndAnExactVersionTwoBackup()
    {
        using var directory = new TemporaryDirectory();
        var original = CreateStore(directory.StatePath, new SpeakersOptions { SpeakersInfo = [Speaker("sensor", "client")] });
        original.Save(original.Current with
        {
            Speakers = original.Current.Speakers.Select(speaker => speaker with { Volume = 37, FullVolumeDistance = 777, MuteDistance = 4444 }).ToList(),
            Groups = [PersistentStateFactory.CreateDefaultGroup(original.Current.Speakers.Select(speaker => speaker.SpeakerId!).ToArray(), TestServices.SourceIds, "automatic")
                with { SourcePriority = ["laptop", "spotify"], MasterVolume = 43, Muted = true,
                    SourceLevels = new() { ["spotify"] = 25, ["laptop"] = 85 } }],
        });
        string saved = File.ReadAllText(directory.StatePath);
        var options = Options.Create(new PlaybackOptions { Sources = [new() { Id = "spotify", Name = "Spotify" },
            new() { Id = "laptop", Name = "PC audio" }], ProfileSessions = true });
        SystemStateStore Load() => new(Options.Create(new StateOptions { FilePath = directory.StatePath }),
            Options.Create(new SpeakersOptions()), options, NullLogger<SystemStateStore>.Instance);
        var migrated = Load();
        Assert.Equal(3, migrated.Current.Version);
        Assert.Empty(migrated.Current.Profiles);
        Assert.False(migrated.Current.PlaybackActivated);
        Assert.Equal(original.Current.StateId, migrated.Current.StateId);
        Assert.Equal(original.Current.Speakers, migrated.Current.Speakers);
        Assert.Equal(43, migrated.Current.Groups.Single().MasterVolume);
        Assert.True(migrated.Current.Groups.Single().Muted);
        Assert.Equal(original.Current.Groups.Single().SpeakerIds, migrated.Current.Groups.Single().SpeakerIds);
        Assert.Equal(original.Current.Groups.Single().SourceLevels, migrated.Current.Groups.Single().SourceLevels);
        string backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(directory.StatePath)!, "*.v2.*.backup"));
        Assert.Equal(saved, File.ReadAllText(backup));
        string configuration = File.ReadAllText(directory.StatePath);
        Assert.Contains("enabledSources", configuration);
        Assert.DoesNotContain("volumeMode", configuration);
        Assert.Equal(migrated.Current.Groups.Single().SourcePriority, Load().Current.Groups.Single().SourcePriority);
        Assert.Throws<InvalidDataException>(() => CreateStore(directory.StatePath));
    }

    [Fact]
    public void ReloadsSourceBalanceWithoutChangingMaster()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.StatePath);
        store.Save(store.Current with
        {
            Groups = [PersistentStateFactory.CreateDefaultGroup([], TestServices.SourceIds, "manual")
                with { MasterVolume = 60, SourceLevels = new() { ["spotify"] = 45, ["laptop"] = 90 } }],
        });
        var restored = CreateStore(directory.StatePath).Current.Groups.Single();
        Assert.Equal(60, restored.MasterVolume);
        Assert.Equal(45, restored.SourceLevels["spotify"]);
        Assert.Equal(90, restored.SourceLevels["laptop"]);
    }

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

    // AirPlay is configured as a source, but a migrated default group never turns it on by itself.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionOneMigrationGroupsAllSpeakersWithConfiguredSources(bool casting)
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

        var store = CreateStore(directory.StatePath, speakerOptions, casting);

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
                  "sourcePriority": ["spotify", "radio"], "volumeMode": "manual", "masterVolume": 80,
                  "sourceLevels": { "spotify": 60, "radio": 35 }
                }
              ]
            }
            """);

        var store = CreateStore(directory.StatePath);

        PersistentPlaybackGroup group = Assert.Single(store.Current.Groups);
        Assert.Equal(["spotify"], group.SourcePriority);
        Assert.Equal(new Dictionary<string, double> { ["spotify"] = 60 }, group.SourceLevels);
        Assert.Equal(4, store.Current.Revision);
        Assert.Equal(["spotify"], CreateStore(directory.StatePath).Current.Groups.Single().SourcePriority);
    }

    [Fact]
    public void StaleSourceLevelsAreDroppedEvenWhenPriorityIsClean()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, $$"""
            {
              "version": 2,
              "stateId": "{{Guid.NewGuid()}}",
              "revision": 3,
              "speakers": [],
              "groups": [
                {
                  "id": "office", "name": "Office", "speakerIds": [],
                  "sourcePriority": ["spotify"], "volumeMode": "manual", "masterVolume": 80,
                  "sourceLevels": { "spotify": 60, "radio": 35 }
                }
              ]
            }
            """);

        var store = CreateStore(directory.StatePath);

        PersistentPlaybackGroup group = Assert.Single(store.Current.Groups);
        Assert.Equal(["spotify"], group.SourcePriority);
        Assert.Equal(new Dictionary<string, double> { ["spotify"] = 60 }, group.SourceLevels);
        Assert.Equal(4, store.Current.Revision);
    }

    [Fact]
    public void StateWithoutCastingFieldsLoadsAndRoundTrips()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, $$"""
            {
              "version": 3,
              "stateId": "{{Guid.NewGuid()}}",
              "revision": 7,
              "speakers": [],
              "groups": [
                {
                  "id": "kantoor", "name": "Kantoor", "speakerIds": [], "enabledSources": ["casting", "spotify"],
                  "masterVolume": 80, "muted": false, "sourceLevels": { "casting": 70, "spotify": 90 }
                }
              ],
              "profiles": [
                { "id": "first", "name": "First", "followMe": false, "sourcePriority": ["spotify", "laptop", "casting"], "overlap": [], "spotifyAccountId": "account" }
              ],
              "sourcePolicies": { "spotify": "playing", "laptop": "connected", "casting": "connected" },
              "sessions": [
                {
                  "id": "session", "producerId": "receiver", "ownerId": "first", "accountId": "account", "source": "spotify",
                  "destination": "kantoor", "state": "playing", "claimSequence": 1, "claimed": true, "eventSequence": 2,
                  "transports": [{ "id": "transport", "kind": "snapcast", "endpoint": "stream", "available": true }]
                }
              ],
              "claimSequence": 1,
              "catalogueRevision": 3,
              "generation": 4,
              "playbackActivated": true
            }
            """);
        var options = Options.Create(new PlaybackOptions
        {
            Sources = TestServices.CreatePlaybackOptions(casting: true).Value.Sources, ProfileSessions = true,
        });
        SystemStateStore Load() => new(Options.Create(new StateOptions { FilePath = directory.StatePath }),
            Options.Create(new SpeakersOptions()), options, NullLogger<SystemStateStore>.Instance);
        var loaded = Load();
        Assert.Equal(7, loaded.Current.Revision);
        PersistentPlaybackGroup group = Assert.Single(loaded.Current.Groups);
        Assert.Equal(["casting", "spotify"], group.SourcePriority);
        Assert.Equal(70, group.SourceLevels["casting"]);
        Assert.Empty(Assert.Single(loaded.Current.Profiles).CastingDevices);
        Assert.Null(loaded.Current.CastingKey);
        PlaybackSession session = Assert.Single(loaded.Current.Sessions);
        Assert.Null(session.Protocol);
        Assert.Null(session.SenderId);
        loaded.Save(loaded.Current);
        string written = File.ReadAllText(directory.StatePath);
        Assert.Contains("\"castingDevices\": []", written);
        Assert.DoesNotContain("senderId", written);
        Assert.DoesNotContain("\"protocol\"", written);
        var reloaded = Load();
        Assert.Equal(JsonSerializer.Serialize(loaded.Current), JsonSerializer.Serialize(reloaded.Current));
        Assert.Equal(written, File.ReadAllText(directory.StatePath));
    }

    [Fact]
    public void CastingSessionsRewrittenByTheRollbackServerAreDroppedOnLoad()
    {
        // The rollback server keeps casting sessions but loses their protocol and senderId on its next write.
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, $$"""
            {
              "version": 3,
              "stateId": "{{Guid.NewGuid()}}",
              "revision": 7,
              "speakers": [],
              "groups": [
                {
                  "id": "kantoor", "name": "Kantoor", "speakerIds": [], "enabledSources": ["casting", "spotify"],
                  "masterVolume": 80, "muted": false, "sourceLevels": { "casting": 70, "spotify": 90 }
                }
              ],
              "profiles": [
                { "id": "first", "name": "First", "followMe": false, "sourcePriority": ["spotify", "laptop", "casting"], "overlap": [], "spotifyAccountId": "account" }
              ],
              "sourcePolicies": { "spotify": "playing", "laptop": "connected", "casting": "connected" },
              "sessions": [
                {
                  "id": "ended", "producerId": "office", "ownerId": "guest:{{new string('a', 64)}}", "accountId": "airplay:device:aa",
                  "source": "casting", "destination": "kantoor", "state": "ended", "claimSequence": 1, "claimed": false, "eventSequence": 3,
                  "transports": [{ "id": "airplay-office", "kind": "snapcast", "endpoint": "airplay-office", "available": false }]
                },
                {
                  "id": "open", "producerId": "house", "ownerId": "guest:{{new string('b', 64)}}", "accountId": "airplay:device:bb",
                  "source": "casting", "destination": "house", "state": "playing", "claimSequence": 2, "claimed": true, "eventSequence": 2,
                  "transports": [{ "id": "airplay-house", "kind": "snapcast", "endpoint": "airplay-house", "available": true }]
                },
                {
                  "id": "spotify", "producerId": "receiver", "ownerId": "first", "accountId": "account", "source": "spotify",
                  "destination": "kantoor", "state": "playing", "claimSequence": 3, "claimed": true, "eventSequence": 2,
                  "transports": [{ "id": "transport", "kind": "snapcast", "endpoint": "stream", "available": true }]
                }
              ],
              "claimSequence": 3,
              "catalogueRevision": 3,
              "generation": 4,
              "playbackActivated": true
            }
            """);
        var options = Options.Create(new PlaybackOptions
        {
            Sources = TestServices.CreatePlaybackOptions(casting: true).Value.Sources, ProfileSessions = true,
        });
        SystemStateStore Load() => new(Options.Create(new StateOptions { FilePath = directory.StatePath }),
            Options.Create(new SpeakersOptions()), options, NullLogger<SystemStateStore>.Instance);
        var loaded = Load();
        Assert.Equal("spotify", Assert.Single(loaded.Current.Sessions).Id);
        Assert.Equal(4, loaded.Current.CatalogueRevision);
        Assert.Equal(3, loaded.Current.ClaimSequence);
        string written = File.ReadAllText(directory.StatePath);
        Assert.DoesNotContain("airplay:device", written);
        var reloaded = Load();
        Assert.Equal(JsonSerializer.Serialize(loaded.Current), JsonSerializer.Serialize(reloaded.Current));
        Assert.Equal(written, File.ReadAllText(directory.StatePath));
    }

    private static SystemStateStore CreateStore(string path, SpeakersOptions? speakerOptions = null, bool casting = false) => new(
        Options.Create(new StateOptions { FilePath = path }),
        Options.Create(speakerOptions ?? new SpeakersOptions()),
        TestServices.CreatePlaybackOptions(casting: casting),
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
