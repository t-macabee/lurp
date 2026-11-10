using MediatR;

namespace ExtractorGate.App;

public class Ping : IRequest<string> { }

public class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken ct) => Task.FromResult("pong");
}
