namespace NexusPipeline.Platform.Windows;

public interface IStartupTaskScheduler : IDisposable
{
    string? Read(string name);
    string Normalize(string xml);
    void Validate(string name, string xml, string userSid);
    void Create(string name, string xml, string userSid);
    void Delete(string name);
}
