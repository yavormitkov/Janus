using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace JanusClearingEngine.Infrastructure.Channels;

public sealed class ClearingFileChannel
{
    private readonly Channel<string> _channel;

    public ClearingFileChannel()
    {
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    public ChannelReader<string> Reader => _channel.Reader;

    public ChannelWriter<string> Writer => _channel.Writer;

    public void Enqueue(string filePath)
    {
        _channel.Writer.TryWrite(filePath);
    }
}
