using MediatR;

namespace ExtractorGate.App;

public class Ping : IRequest<string> { }
public class Pong : IRequest<string> { }

public class PingHandler : IRequestHandler<Ping, string>, IRequestHandler<Pong, string>
{
    public Task<string> Handle(Ping request, CancellationToken ct) => Task.FromResult("pong");
    public Task<string> Handle(Pong request, CancellationToken ct) => Task.FromResult("pong");
}

public class StreamPing : IStreamRequest<string> { }

public class StreamHandler : IStreamRequestHandler<StreamPing, string>
{
    public async IAsyncEnumerable<string> Handle(
        StreamPing request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return await Task.FromResult("pong");
    }
}
