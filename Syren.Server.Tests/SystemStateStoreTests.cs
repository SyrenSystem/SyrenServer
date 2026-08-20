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
        string directory = Path.Combine(Path.GetTempPath(), $"syren-state-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "state.json");
        try
        {
            var options = Options.Create(new StateOptions { FilePath = path });
            var firstStore = new SystemStateStore(options);
            firstStore.Save(new PersistentSystemState
            {
                StateId = firstStore.Current.StateId,
                Speakers =
                [
                    new PersistentSpeakerState
                    {
                        SensorId = "sensor",
                        Connected = false,
                        Volume = 42,
                    },
                ],
            });

            var secondStore = new SystemStateStore(options);

            Assert.Equal(firstStore.Current.StateId, secondStore.Current.StateId);
            Assert.Equal(42, secondStore.Current.Speakers.Single().Volume);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CorruptStateStopsLoading()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"syren-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.json");
        try
        {
            File.WriteAllText(path, "not json");

            Assert.Throws<InvalidDataException>(() => new SystemStateStore(
                Options.Create(new StateOptions { FilePath = path })
            ));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
