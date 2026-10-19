using System.Text.Json;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Bounded local file journal for one installation in a private, existing directory.
/// Cooperating processes must use the same directory on a local filesystem. Never copy/delete its
/// lock file while in use. This is not a remote NAS lock, ownership proof or power-loss guarantee.</summary>
public sealed class FileOperationJournal : IOperationPlanJournal
{
    private readonly object gate = new();

    private readonly string directory;

    private readonly OperationJournalScope scope;

    private Lease? activeLease;

    private string SnapshotPath => Path.Combine(directory, "journal.json");

    private string LockPath => Path.Combine(directory, "journal.lock");

    private FileOperationJournal(string directory, OperationJournalScope scope)
    {
        if (scope is not { IsValid: true })
            throw new OperationJournalException("journal.identity");
        this.directory = Path.GetFullPath(directory);
        this.scope = scope;
        ValidatePaths();
    }

    /// <summary>Explicit initialization only. Existing lock/store is never reset, even if corrupt.
    /// Caller must select a private, non-linked local directory unique to this installation.</summary>
    public static FileOperationJournal Create(string directory, OperationJournalScope scope)
    {
        return Guard(() =>
        {
            var journal = new FileOperationJournal(directory, scope);
            using var fileLock = journal.OpenLock(FileMode.CreateNew);
            journal.Save(OperationJournalSnapshot.Create(scope, 0, []), replace: false);
            return journal;
        });
    }

    /// <summary>Open only a complete compatible store with a known scope; missing data is an error.</summary>
    public static FileOperationJournal Open(string directory, OperationJournalScope scope)
    {
        return Guard(() =>
        {
            var journal = new FileOperationJournal(directory, scope);
            using var fileLock = journal.OpenLock(FileMode.Open);
            journal.Load();
            return journal;
        });
    }

    public Task BindPlanAsync(ConfigurationOperationPlan plan, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RunAsync(() =>
        {
            if (activeLease is not null)
                throw new OperationLeaseUnavailableException();
            using var fileLock = OpenLock(FileMode.Open);
            var snapshot = Load();
            var resource = plan.Steps[0].Descriptor.Identity.Resource;
            if (resource.InstallationId != scope.InstallationId || resource.ProfileFingerprint != scope.ProfileFingerprint)
                throw new OperationPlanMismatchException();
            var binding = OperationPlanBinding.From(plan);
            binding.Validate(scope);
            if (snapshot.Data.Plan is { } previous)
            {
                if (previous.Fingerprint != binding.Fingerprint)
                    throw new OperationPlanMismatchException();
                return true;
            }
            // An existing single-operation history cannot be silently adopted into a new graph.
            if (snapshot.Data.Entries.Length != 0)
                throw new OperationPlanMismatchException();
            Save(OperationJournalSnapshot.Create(scope, checked(snapshot.Data.Generation + 1), [], binding), replace: true);
            return true;
        }, token);
    }

    public IOperationJournal ForPlanStep(OperationPlanStepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new PlanStepJournal(this, context);
    }

    public Task<IAsyncDisposable> AcquireAsync(OperationDescriptor descriptor, CancellationToken token) =>
        AcquireAsync(descriptor, null, token);

    private Task<IAsyncDisposable> AcquireAsync(OperationDescriptor descriptor, OperationPlanStepContext? context, CancellationToken token)
    {
        return RunAsync<IAsyncDisposable>(() =>
        {
            if (activeLease is not null)
                throw new OperationLeaseUnavailableException();
            var lease = Acquire(descriptor, context);
            activeLease = lease;
            return lease;
        }, token);
    }

    private Lease Acquire(OperationDescriptor descriptor, OperationPlanStepContext? context)
    {
        OperationJournalSnapshot.ValidateDescriptor(descriptor, scope);
        var fileLock = OpenLock(FileMode.Open);
        try
        {
            var snapshot = Load();
            if (snapshot.Data.Plan is { } plan)
            {
                plan.RequireMember(descriptor);
                if (context is null || !context.Authorizes(plan.Fingerprint, descriptor))
                    throw new OperationPlanMismatchException();
            }
            else if (context is not null)
            {
                throw new OperationPlanMismatchException();
            }
            FindEntry(snapshot, descriptor, observePlanStep: true);
            return new Lease(this, descriptor, fileLock, snapshot.Hash);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    public Task<OperationJournalEntry?> ReadAsync(OperationDescriptor descriptor, CancellationToken token)
    {
        return RunAsync(() => FindEntry(Current(descriptor), descriptor, observePlanStep: true), token);
    }

    public Task RecordIntentAsync(OperationIntent intent, CancellationToken token)
    {
        return RunAsync(() =>
        {
            OperationJournalSnapshot.ValidateIntent(intent, scope);
            var snapshot = Current(intent.Descriptor);
            var previous = FindEntry(snapshot, intent.Descriptor);
            if (previous is not null)
            {
                if (previous.Confirmation is not null)
                    throw new OperationJournalException("journal.confirm-conflict");
                if (previous.Intent != intent)
                    throw new OperationJournalException("journal.intent-conflict");
                return true;
            }
            if (snapshot.Data.Entries.Length >= OperationJournalSnapshot.MaximumEntries)
                throw new OperationJournalException("journal.capacity");
            Commit(snapshot, [.. snapshot.Data.Entries, new OperationJournalEntry(intent)]);
            return true;
        }, token);
    }

    public Task ConfirmAsync(OperationIntent intent, OperationVerification verification, CancellationToken token)
    {
        return RunAsync(() =>
        {
            OperationJournalSnapshot.ValidateIntent(intent, scope);
            if (verification is null || !verification.IsSatisfiedFor(intent.Descriptor))
                throw new OperationJournalException("journal.verification");
            var snapshot = Current(intent.Descriptor);
            var previous = FindEntry(snapshot, intent.Descriptor);
            if (previous is null || previous.Intent != intent)
                throw new OperationJournalException("journal.intent-conflict");
            if (previous.Confirmation is not null)
            {
                if (previous.Confirmation != verification)
                    throw new OperationJournalException("journal.confirm-conflict");
                return true;
            }
            var entries = snapshot.Data.Entries.Select(entry => entry == previous
                ? new OperationJournalEntry(intent, verification)
                : entry).ToArray();
            Commit(snapshot, entries);
            return true;
        }, token);
    }

    private Task<T> RunAsync<T>(Func<T> action, CancellationToken token)
    {
        // Keep bounded file work off the caller's UI thread. Once committing, finish publication;
        // cancellation after an intent may not erase it or imply that the external write stopped.
        return Task.Run(() =>
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                return Guard(action);
            }
        }, token);
    }

    private OperationJournalSnapshot Current(OperationDescriptor descriptor)
    {
        if (activeLease is null || activeLease.Descriptor != descriptor)
            throw new OperationJournalException("journal.lease-required");
        var snapshot = Load();
        if (snapshot.Hash != activeLease.Hash)
            throw new OperationJournalException("journal.changed");
        return snapshot;
    }

    private static OperationJournalEntry? FindEntry(OperationJournalSnapshot snapshot, OperationDescriptor descriptor, bool observePlanStep = false)
    {
        OperationJournalEntry? found = null;
        foreach (var entry in snapshot.Data.Entries)
        {
            var identity = entry.Intent.Descriptor.Identity;
            if (identity.Resource.ResourceId == descriptor.Identity.Resource.ResourceId)
            {
                if (identity.Resource != descriptor.Identity.Resource)
                    throw new OperationJournalException("journal.identity");
                var differentPendingOperation = entry.Confirmation is null
                    && identity.OperationId != descriptor.Identity.OperationId;
                // A bound graph must reobserve earlier steps even when a later step of the same
                // resource is pending. This is read permission only: RecordIntent still uses the
                // strict path and cannot create another pending write for that resource.
                if (differentPendingOperation && !(observePlanStep && snapshot.Data.Plan is not null))
                    throw new OperationJournalException("journal.pending-conflict");
            }
            if (identity.OperationId == descriptor.Identity.OperationId)
            {
                if (entry.Intent.Descriptor != descriptor)
                    throw new OperationJournalException("journal.descriptor");
                found = entry;
            }
        }
        return found;
    }

    private void Commit(OperationJournalSnapshot previous, OperationJournalEntry[] entries)
    {
        var next = OperationJournalSnapshot.Create(scope, checked(previous.Data.Generation + 1), entries, previous.Data.Plan);
        Save(next, replace: true);
        activeLease!.Hash = next.Hash;
    }

    private OperationJournalSnapshot Load()
    {
        ValidatePaths();
        using var stream = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > OperationJournalSnapshot.MaximumBytes)
            throw new OperationJournalException("journal.size");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new OperationJournalException("journal.changed");
        return OperationJournalSnapshot.Decode(bytes, scope);
    }

    private void Save(OperationJournalSnapshot snapshot, bool replace)
    {
        var bytes = snapshot.Encode();
        ValidatePaths();
        var temporary = Path.Combine(directory, $"journal-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, Options(FileMode.CreateNew)))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ValidatePaths();
            File.Move(temporary, SnapshotPath, overwrite: replace);
        }
        finally
        {
            // A failed cleanup is also a storage failure; an orphan is never promoted on open.
            File.Delete(temporary);
        }
    }

    private FileStream OpenLock(FileMode mode)
    {
        ValidatePaths();
        try
        {
            return new FileStream(LockPath, Options(mode));
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            throw new OperationLeaseUnavailableException();
        }
    }

    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows() && mode == FileMode.CreateNew)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private void ValidatePaths()
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new OperationJournalException("journal.directory");
        }
        RejectLink(SnapshotPath);
        RejectLink(LockPath);
    }

    private static void RejectLink(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new OperationJournalException("journal.link");
    }

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (OperationJournalException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw new OperationJournalException("journal.storage");
        }
    }

    private sealed class PlanStepJournal(FileOperationJournal owner, OperationPlanStepContext context) : IOperationJournal
    {
        public Task<IAsyncDisposable> AcquireAsync(OperationDescriptor descriptor, CancellationToken token) =>
            owner.AcquireAsync(descriptor, context, token);

        public Task<OperationJournalEntry?> ReadAsync(OperationDescriptor descriptor, CancellationToken token) => owner.ReadAsync(descriptor, token);

        public Task RecordIntentAsync(OperationIntent intent, CancellationToken token) => owner.RecordIntentAsync(intent, token);

        public Task ConfirmAsync(OperationIntent intent, OperationVerification verification, CancellationToken token) =>
            owner.ConfirmAsync(intent, verification, token);
    }

    private sealed class Lease(FileOperationJournal owner, OperationDescriptor descriptor, FileStream fileLock, string hash) : IAsyncDisposable
    {
        public OperationDescriptor Descriptor { get; } = descriptor;

        public string Hash { get; set; } = hash;

        public ValueTask DisposeAsync()
        {
            lock (owner.gate)
            {
                if (owner.activeLease == this)
                {
                    fileLock.Dispose();
                    owner.activeLease = null;
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}
