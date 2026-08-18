namespace Garius.Infrastructure.RateLimiting;

/// <summary>
/// Seção <c>RateLimit</c>. Limites por <b>IP real</b> (o da Cloudflare, validado — ver
/// <c>RealIpMiddleware</c>), com contadores no Redis para valerem entre réplicas.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>
    /// Desligar isto em produção é uma decisão de segurança, não de configuração. Existe para
    /// testes e para depuração local.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Teto <b>geral</b> por IP. Generoso de propósito: existe para conter um cliente
    /// descontrolado (um retry-loop mal escrito, um scraper), não para policiar uso normal.
    /// Um limite global apertado quebraria o front, que faz várias chamadas por tela.
    /// </summary>
    public RateLimitRule Global { get; set; } = new() { PermitLimit = 100, WindowSeconds = 60 };

    /// <summary>
    /// <b>Login.</b> Agressivo, porque é a superfície clássica de brute force.
    ///
    /// <para>
    /// Esta é uma dimensão <b>independente</b> do lockout do Identity. O lockout é por
    /// <b>conta</b> (protege uma conta contra mil senhas); este é por <b>IP</b> (protege mil
    /// contas contra uma senha — o <i>password spraying</i>, que passa despercebido pelo
    /// lockout porque cada conta erra uma única vez). Faltando uma das duas, um dos dois
    /// ataques passa. O template anterior tinha só a de IP.
    /// </para>
    /// </summary>
    public RateLimitRule Login { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60 };

    /// <summary>
    /// <b>Emissão de token M2M.</b> Um client <b>não é uma conta</b>: não tem lockout, e
    /// portanto <c>/auth/token</c> nasceu (na Fase 4c) como uma superfície de brute force
    /// <b>sem nenhuma proteção</b>. Este limite é a única que ela tem.
    ///
    /// <para>
    /// ⚠️ <b>Particionado só por IP</b>, não por <c>client_id</c>. Particionar pelo client_id
    /// exigiria <b>ler o corpo</b> da requisição dentro do middleware (<c>EnableBuffering</c> +
    /// rebobinar o stream) — e errar isso entrega um corpo vazio ao endpoint, que passaria a
    /// rejeitar todo login M2M legítimo. A defesa que fica de fora é contra um atacante com
    /// <b>IPs rotativos</b> martelando um único client_id. Aceito conscientemente: o segredo do
    /// client tem 256 bits de entropia (não é uma senha humana), então não há dicionário
    /// viável contra ele — o brute force que este limite contém é o do <i>volume</i>, não o do
    /// acerto.
    /// </para>
    /// </summary>
    public RateLimitRule Token { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>
    /// <b>Refresh.</b> Um cliente legítimo renova a sessão de hora em hora — não vinte vezes
    /// por minuto. Um volume alto aqui é sinal de token sendo testado.
    /// </summary>
    public RateLimitRule Refresh { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    /// <summary>
    /// <b>Por identidade</b> (usuário, client M2M ou chave de API) — a segunda dimensão.
    ///
    /// <para>
    /// Limite só por IP erra dos dois lados: pune o cliente legítimo atrás de CGNAT (milhares
    /// de pessoas dividindo um endereço, e portanto uma cota) e não contém o atacante com um
    /// <c>/64</c> de IPv6, que tem endereços de sobra para diluir o volume. Esta regra fecha os
    /// dois casos, e é aplicada <b>depois</b> da autorização — ver
    /// <c>IdentityRateLimitMiddleware</c>, que explica por que a ordem tem de ser essa.
    /// </para>
    ///
    /// <para>
    /// Mais folgado que o global por IP, de propósito: aqui já se sabe QUEM está chamando, e um
    /// cliente autenticado legítimo faz mais requisições que um anônimo.
    /// </para>
    /// </summary>
    public IdentityRateLimitRule Identity { get; set; } = new();
}

/// <summary>O limite por identidade, que pode ser desligado sem mexer no resto.</summary>
public sealed class IdentityRateLimitRule
{
    /// <summary>
    /// Separado do <c>RateLimit:Enabled</c> global: dá para desligar só esta camada (numa
    /// investigação, por exemplo) sem derrubar junto a defesa por IP, que é a que contém
    /// tráfego anônimo abusivo.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int PermitLimit { get; set; } = 300;

    public int WindowSeconds { get; set; } = 60;

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>Um limite: quantas requisições, em qual janela.</summary>
public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
