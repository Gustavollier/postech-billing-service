using PosTech.Mensageria;

namespace PosTech.Billing.Testes;

/// <summary>Outbox em memória, com o mesmo comportamento que a tabela precisa ter.</summary>
public sealed class OutboxEmMemoria : IRepositorioDeOutbox
{
    private long _proximoId = 1;

    public List<EventoOutbox> Linhas { get; } = [];

    public EventoOutbox Gravar(EventoOutbox evento)
    {
        evento.Id = _proximoId++;
        Linhas.Add(evento);
        return evento;
    }

    public Task<IReadOnlyList<EventoOutbox>> ObterPendentesAsync(int limite, CancellationToken cancellationToken = default)
    {
        var agora = DateTime.UtcNow;

        IReadOnlyList<EventoOutbox> pendentes = Linhas
            .Where(l => l.Status == EStatusOutbox.Pendente && l.ProximaTentativaEm <= agora)
            .OrderBy(l => l.Id)
            .Take(limite)
            .ToList();

        return Task.FromResult(pendentes);
    }

    public Task MarcarPublicadoAsync(Guid mensagemId, DateTime publicadoEm, CancellationToken cancellationToken = default)
    {
        var linha = Linhas.Single(l => l.MensagemId == mensagemId);
        linha.Status = EStatusOutbox.Publicado;
        linha.PublicadoEm = publicadoEm;
        return Task.CompletedTask;
    }

    public Task RegistrarFalhaAsync(
        Guid mensagemId, int tentativas, DateTime proximaTentativaEm, string erro, EStatusOutbox status,
        CancellationToken cancellationToken = default)
    {
        var linha = Linhas.Single(l => l.MensagemId == mensagemId);
        linha.Tentativas = tentativas;
        linha.ProximaTentativaEm = proximaTentativaEm;
        linha.UltimoErro = erro;
        linha.Status = status;
        return Task.CompletedTask;
    }
}

/// <summary>Publicador que obedece ou falha sob comando, para exercitar o retry.</summary>
public sealed class PublicadorFalso : IPublicadorDeEventos
{
    public List<EventoOutbox> Publicados { get; } = [];

    public int FalharNasPrimeiras { get; set; }

    public int Chamadas { get; private set; }

    public Task PublicarAsync(EventoOutbox evento, CancellationToken cancellationToken = default)
    {
        Chamadas++;

        if (Chamadas <= FalharNasPrimeiras)
            throw new InvalidOperationException("broker indisponível");

        Publicados.Add(evento);
        return Task.CompletedTask;
    }
}

/// <summary>Registro de idempotência em memória, com a mesma chave composta da tabela.</summary>
public sealed class IdempotenciaEmMemoria : IRegistroDeIdempotencia
{
    private readonly HashSet<(Guid, string)> _vistos = [];

    public Task<bool> JaProcessadoAsync(Guid mensagemId, string consumidor, CancellationToken cancellationToken = default) =>
        Task.FromResult(_vistos.Contains((mensagemId, consumidor)));

    public Task RegistrarAsync(Guid mensagemId, string tipo, string consumidor, DateTime quando, CancellationToken cancellationToken = default)
    {
        _vistos.Add((mensagemId, consumidor));
        return Task.CompletedTask;
    }
}
