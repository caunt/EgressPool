using System.Diagnostics;
using System.Text.Json;

namespace Egress.Internal;

internal sealed class OwnedNetworkStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
    };

    private readonly string statePath;
    private readonly string lockPath;

    private OwnedNetworkStateStore(string statePath)
    {
        this.statePath = statePath;
        lockPath = statePath + ".lock";
    }

    internal static DateTimeOffset CurrentProcessStartTimeUtc { get; } = GetCurrentProcessStartTimeUtc();

    internal static OwnedNetworkStateStore Create(EgressCleanupOptions options)
    {
        string stateDirectory = options.StateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgressPool");

        Directory.CreateDirectory(stateDirectory);
        return new OwnedNetworkStateStore(Path.Combine(stateDirectory, "owned-network-state.json"));
    }

    internal OwnedNetworkStateEntry AddPending(OwnedNetworkStateEntry entry)
    {
        WithLockedEntries(entries =>
        {
            entries.RemoveAll(existingEntry => existingEntry.Id == entry.Id);
            entries.Add(entry);
        });

        return entry;
    }

    internal void MarkCreated(string entryId)
    {
        WithLockedEntries(entries =>
        {
            int entryIndex = entries.FindIndex(entry => entry.Id == entryId);
            if (entryIndex < 0)
            {
                throw new InvalidOperationException($"Owned network state entry '{entryId}' was not found.");
            }

            entries[entryIndex] = entries[entryIndex] with
            {
                Status = OwnedNetworkStateStatus.Created,
            };
        });
    }

    internal void Remove(string entryId)
    {
        WithLockedEntries(entries => entries.RemoveAll(entry => entry.Id == entryId));
    }

    internal IReadOnlyList<OwnedNetworkStateEntry> GetStaleEntries(string platformName)
    {
        return WithLockedEntries(entries =>
        {
            List<OwnedNetworkStateEntry>? staleEntries = null;
            for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                OwnedNetworkStateEntry entry = entries[entryIndex];
                if (!string.Equals(entry.PlatformName, platformName, StringComparison.OrdinalIgnoreCase) || !IsStale(entry))
                {
                    continue;
                }

                staleEntries ??= [];
                staleEntries.Add(entry);
            }

            return (IReadOnlyList<OwnedNetworkStateEntry>?)staleEntries ?? Array.Empty<OwnedNetworkStateEntry>();
        });
    }

    private static bool IsStale(OwnedNetworkStateEntry entry)
    {
        if (entry.Status == OwnedNetworkStateStatus.Pending)
        {
            return true;
        }

        try
        {
            using Process process = Process.GetProcessById(entry.OwnerProcessId);
            DateTimeOffset actualStartTimeUtc = process.StartTime.ToUniversalTime();
            return actualStartTimeUtc != entry.OwnerProcessStartTimeUtc;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void WithLockedEntries(Action<List<OwnedNetworkStateEntry>> action)
    {
        _ = WithLockedEntries(entries =>
        {
            action(entries);
            return true;
        });
    }

    private T WithLockedEntries<T>(Func<List<OwnedNetworkStateEntry>, T> action)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        using FileStream lockStream = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        List<OwnedNetworkStateEntry> entries = ReadEntries();
        T result = action(entries);
        WriteEntries(entries);
        return result;
    }

    private List<OwnedNetworkStateEntry> ReadEntries()
    {
        if (!File.Exists(statePath))
        {
            return [];
        }

        string json = File.ReadAllText(statePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<OwnedNetworkStateEntry>>(json, SerializerOptions) ?? [];
    }

    private void WriteEntries(List<OwnedNetworkStateEntry> entries)
    {
        string temporaryPath = statePath + ".tmp";
        string json = JsonSerializer.Serialize(entries, SerializerOptions);
        File.WriteAllText(temporaryPath, json);

        if (File.Exists(statePath))
        {
            File.Copy(temporaryPath, statePath, overwrite: true);
            File.Delete(temporaryPath);
        }
        else
        {
            File.Move(temporaryPath, statePath);
        }
    }

    private static DateTimeOffset GetCurrentProcessStartTimeUtc()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
