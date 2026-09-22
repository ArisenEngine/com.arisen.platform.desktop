namespace ArisenEngine.Platform;

public abstract class MessageHandler : IMessageHandler
{
    public abstract bool NextFrame();

    /// <summary>
    /// Blocks until the thread this handler pumps has a message to process. The primitive a frame
    /// loop parks on while its window cannot composite presented frames.
    /// </summary>
    public abstract void WaitForMessage();
}

public interface IMessageHandler
{
    bool NextFrame();

    void WaitForMessage();
}
