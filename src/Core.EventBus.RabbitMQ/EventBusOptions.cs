public class EventBusOptions
{
    public string SubscriptionClientName { get; set; } = "UnknownService";
    public int RetryCount { get; set; } = 10;
    public string HostName { get; set; } = "localhost";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}