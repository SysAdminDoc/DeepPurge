using System.Runtime.InteropServices;

namespace DeepPurge.Core.Schedule;

internal sealed record TaskSchedulerTaskRecord(
    string Name,
    string Xml,
    string SecurityDescriptor);

internal interface ITaskSchedulerBackend
{
    IReadOnlyList<TaskSchedulerTaskRecord> List();
    TaskSchedulerTaskRecord? Get(string name);
    void Register(
        string name,
        string xml,
        string principalSid,
        string taskSecurityDescriptor,
        string folderSecurityDescriptor);
    void Delete(string name);
}

/// <summary>
/// Minimal Task Scheduler 2.0 automation adapter. XML registration keeps the
/// executable and argument fields distinct and makes the security-sensitive
/// definition independently testable without invoking schtasks.exe.
/// </summary>
internal sealed class WindowsTaskSchedulerBackend : ITaskSchedulerBackend
{
    private const string FolderPath = @"\DeepPurge";
    private const int IncludeHiddenTasks = 1;
    private const int CreateOrUpdate = 6;
    private const int InteractiveToken = 3;
    private const int OwnerGroupDaclSecurityInformation = 7;

    public IReadOnlyList<TaskSchedulerTaskRecord> List()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<TaskSchedulerTaskRecord>();

        object? serviceObject = null;
        object? folderObject = null;
        object? collectionObject = null;
        try
        {
            serviceObject = Connect();
            dynamic service = serviceObject;
            try
            {
                folderObject = service.GetFolder(FolderPath);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                return Array.Empty<TaskSchedulerTaskRecord>();
            }

            dynamic folder = folderObject;
            collectionObject = folder.GetTasks(IncludeHiddenTasks);
            dynamic collection = collectionObject;
            var records = new List<TaskSchedulerTaskRecord>();
            for (var index = 1; index <= (int)collection.Count; index++)
            {
                object? taskObject = null;
                try
                {
                    taskObject = collection.Item(index);
                    dynamic task = taskObject;
                    records.Add(ReadRecord(task));
                }
                finally
                {
                    Release(taskObject);
                }
            }
            return records;
        }
        finally
        {
            Release(collectionObject);
            Release(folderObject);
            Release(serviceObject);
        }
    }

    public TaskSchedulerTaskRecord? Get(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;

        object? serviceObject = null;
        object? folderObject = null;
        object? taskObject = null;
        try
        {
            serviceObject = Connect();
            dynamic service = serviceObject;
            try
            {
                folderObject = service.GetFolder(FolderPath);
                dynamic folder = folderObject;
                taskObject = folder.GetTask(name);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                return null;
            }

            dynamic task = taskObject;
            return ReadRecord(task);
        }
        finally
        {
            Release(taskObject);
            Release(folderObject);
            Release(serviceObject);
        }
    }

    public void Register(
        string name,
        string xml,
        string principalSid,
        string taskSecurityDescriptor,
        string folderSecurityDescriptor)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Task Scheduler is available only on Windows.");

        object? serviceObject = null;
        object? rootObject = null;
        object? folderObject = null;
        object? registeredObject = null;
        try
        {
            serviceObject = Connect();
            dynamic service = serviceObject;
            rootObject = service.GetFolder(@"\");
            dynamic root = rootObject;
            try
            {
                folderObject = service.GetFolder(FolderPath);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                folderObject = root.CreateFolder("DeepPurge", folderSecurityDescriptor);
            }

            dynamic folder = folderObject;
            folder.SetSecurityDescriptor(folderSecurityDescriptor, 0);
            registeredObject = folder.RegisterTask(
                name,
                xml,
                CreateOrUpdate,
                principalSid,
                null,
                InteractiveToken,
                taskSecurityDescriptor);
            dynamic registered = registeredObject;
            registered.SetSecurityDescriptor(taskSecurityDescriptor, 0);
        }
        finally
        {
            Release(registeredObject);
            Release(folderObject);
            Release(rootObject);
            Release(serviceObject);
        }
    }

    public void Delete(string name)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Task Scheduler is available only on Windows.");

        object? serviceObject = null;
        object? folderObject = null;
        try
        {
            serviceObject = Connect();
            dynamic service = serviceObject;
            folderObject = service.GetFolder(FolderPath);
            dynamic folder = folderObject;
            folder.DeleteTask(name, 0);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            // Idempotent removal: an absent task/folder is already deleted.
        }
        finally
        {
            Release(folderObject);
            Release(serviceObject);
        }
    }

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new InvalidOperationException("Task Scheduler 2.0 automation is unavailable.");
        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Task Scheduler 2.0 automation could not be started.");
        dynamic service = instance;
        service.Connect();
        return instance;
    }

    private static TaskSchedulerTaskRecord ReadRecord(dynamic task)
    {
        string securityDescriptor;
        try
        {
            securityDescriptor = (string)task.GetSecurityDescriptor(
                OwnerGroupDaclSecurityInformation);
        }
        catch
        {
            securityDescriptor = string.Empty;
        }

        return new TaskSchedulerTaskRecord(
            (string)task.Name,
            (string)task.Xml,
            securityDescriptor);
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); }
        catch { /* COM cleanup is best-effort after the operation completed. */ }
    }

    private static bool IsNotFound(Exception exception)
        => unchecked((uint)exception.HResult) is 0x80070002 or 0x80070003;
}
