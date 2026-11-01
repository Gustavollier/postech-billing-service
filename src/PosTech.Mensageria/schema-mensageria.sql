-- ---------------------------------------------------------------------------
-- Tabelas de mensageria: outbox de publicação e inbox de idempotência.
--
-- Vai para o banco de CADA serviço que publica ou consome evento. Não existe
-- uma tabela central: o outbox precisa estar no mesmo banco do dado de negócio,
-- senão a gravação e o enfileiramento voltam a ser duas transações e o padrão
-- perde a razão de existir.
--
-- Idempotente, no mesmo formato do schema-azure.sql da Fase 3: pode ser
-- reaplicado sobre um ambiente já provisionado.
-- ---------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EventoOutbox')
BEGIN
    CREATE TABLE EventoOutbox (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        MensagemId UNIQUEIDENTIFIER NOT NULL,
        Tipo NVARCHAR(100) NOT NULL,
        RoutingKey NVARCHAR(100) NOT NULL,
        Payload NVARCHAR(MAX) NOT NULL,
        CorrelationId NVARCHAR(100) NOT NULL,
        -- 0 Pendente, 1 Publicado, 2 Falhou
        Status TINYINT NOT NULL DEFAULT 0,
        Tentativas INT NOT NULL DEFAULT 0,
        CriadoEm DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        PublicadoEm DATETIME2 NULL,
        ProximaTentativaEm DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UltimoErro NVARCHAR(2000) NULL,

        -- Reenviar é normal; publicar duas vezes a mesma mensagem não é.
        CONSTRAINT UQ_EventoOutbox_MensagemId UNIQUE (MensagemId)
    );
END;
GO

-- O publicador roda em laço curto e sempre faz a mesma pergunta: o que está
-- pendente e já pode ser tentado. Sem este índice, cada volta do laço vira um
-- scan na tabela inteira, que cresce para sempre.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EventoOutbox_Pendentes')
BEGIN
    CREATE NONCLUSTERED INDEX IX_EventoOutbox_Pendentes
        ON EventoOutbox (Status, ProximaTentativaEm)
        INCLUDE (MensagemId, Tipo, RoutingKey, Tentativas);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EventoProcessado')
BEGIN
    CREATE TABLE EventoProcessado (
        MensagemId UNIQUEIDENTIFIER NOT NULL,
        Consumidor NVARCHAR(100) NOT NULL,
        Tipo NVARCHAR(100) NOT NULL,
        ProcessadoEm DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

        -- A chave é o par mensagem + consumidor: dois serviços diferentes
        -- processam a mesma mensagem legitimamente, o mesmo serviço não.
        CONSTRAINT PK_EventoProcessado PRIMARY KEY (MensagemId, Consumidor)
    );
END;
GO

-- A tabela de idempotência cresce para sempre se ninguém limpar. Trinta dias
-- cobre com folga qualquer redelivery real; o que chegar depois disso é outro
-- problema, não duplicata.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EventoProcessado_ProcessadoEm')
BEGIN
    CREATE NONCLUSTERED INDEX IX_EventoProcessado_ProcessadoEm
        ON EventoProcessado (ProcessadoEm);
END;
GO

-- ---------------------------------------------------------------------------
-- Estado da saga. Vai só para o banco do OS Service, que é quem orquestra.
--
-- Mora ao lado da ordem de propósito: a saga e a ordem mudam juntas, e separá-las
-- criaria exatamente a transação distribuída que o padrão existe para evitar.
-- ---------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SagaOrdemServico')
BEGIN
    CREATE TABLE SagaOrdemServico (
        IdOrdem INT NOT NULL PRIMARY KEY,
        -- 0 AguardandoDiagnostico, 1 AguardandoOrcamento, 2 AguardandoAprovacao,
        -- 3 AguardandoPagamento, 4 AguardandoExecucao, 5 Concluida,
        -- 6 Compensando, 7 Cancelada
        Passo TINYINT NOT NULL DEFAULT 0,
        StatusDaOrdem TINYINT NOT NULL DEFAULT 0,
        IdOrcamento NVARCHAR(50) NULL,
        IdPagamento NVARCHAR(50) NULL,
        -- Id da cobrança no Mercado Pago. Sem ele não dá para estornar.
        ReferenciaExterna NVARCHAR(100) NULL,
        Valor DECIMAL(10, 2) NULL,
        MotivoDaCompensacao NVARCHAR(500) NULL,
        AtualizadoEm DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- O varredor de timeout pergunta sempre a mesma coisa: quais sagas estão num
-- passo há tempo demais. Sem o índice, isso vira scan da tabela inteira a cada
-- volta, e ela cresce com o movimento da oficina.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SagaOrdemServico_Paradas')
BEGIN
    CREATE NONCLUSTERED INDEX IX_SagaOrdemServico_Paradas
        ON SagaOrdemServico (Passo, AtualizadoEm);
END;
GO
