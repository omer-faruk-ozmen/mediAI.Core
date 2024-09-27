namespace Core.CrossCuttingConcerns.Logging.Configurations;

public class FileLogConfiguration(string folderPath)
{
    public string FolderPath { get; set; } = folderPath;

    public FileLogConfiguration() : this(string.Empty)
    {
    }
}
