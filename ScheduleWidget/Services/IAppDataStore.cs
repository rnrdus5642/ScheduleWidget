using System;

namespace ScheduleWidget
{
    public interface IAppDataStore
    {
        DataLoadResult LoadData();
        void SaveData(AppData data);
    }

    public sealed class DataLoadResult
    {
        public DataLoadResult(AppData data, string warningMessage = null)
        {
            Data = data ?? new AppData();
            WarningMessage = warningMessage;
        }

        public AppData Data { get; private set; }
        public string WarningMessage { get; private set; }
    }

    public sealed class DataStorageException : Exception
    {
        public DataStorageException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
