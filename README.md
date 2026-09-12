# PostgreSQL Command Executer

Executor desktop de SQL para PostgreSQL em `localhost`, feito em C# com WPF e .NET Framework 4.8.

## Recursos

- conexão limitada no código a `127.0.0.1`;
- editor SQL com coloração de palavras-chave, strings, números e comentários;
- `Ctrl+Enter` executa a seleção ou, sem seleção, todo o editor;
- resultado virtualizado em tabela, com cabeçalho copiável;
- visualização alternativa em JSON formatado;
- cancelamento da consulta;
- abertura e salvamento de arquivos `.sql`;
- mensagens do PostgreSQL com SQLSTATE, detalhe e dica.
- temas claro e escuro com troca instantânea pelo menu **Tema** ou pelo botão do editor.
- painel de resultados expansível, preservando a área de conexão e ocultando temporariamente o editor.

As duas paletas definem explicitamente cores de texto, superfícies, seleção, cabeçalhos, células e syntax highlighting. Isso evita que controles nativos do WPF misturem texto claro com fundos claros.

A barra de menu possui uma superfície distinta nos dois temas. O aplicativo usa um ícone próprio de banco de dados e execução SQL na janela e no executável.

Use o botão **Expandir** sobre o painel de resultados ou `Ctrl+Shift+Espaço`. Pressione `Esc` para restaurar o editor. A proporção ajustada manualmente pelo divisor é preservada ao restaurar.

## Pré-requisitos para compilar pelo terminal

Na máquina de desenvolvimento, instale:

1. **Visual Studio Build Tools 2022** (ou 2019);
2. workload **Desenvolvimento para desktop com .NET**;
3. **.NET Framework 4.8 Developer Pack/Targeting Pack**;
4. componente **NuGet targets and build tasks**.

O programa gerado requer apenas o **.NET Framework 4.8 Runtime** na máquina Windows Server 2012. A máquina também precisa ter suporte gráfico/Desktop Experience para executar WPF.

## PostgreSQL local com Docker

O ambiente usa PostgreSQL 18 e publica a porta exclusivamente em `127.0.0.1`, sem exposição na rede.

Suba o banco:

```powershell
.\database-up.ps1
```

Credenciais padrão para desenvolvimento:

```text
Servidor: 127.0.0.1
Porta:    55432
Banco:    postgres_executor_dev
Usuário:  executor_dev
Senha:    local_dev_password
```

O banco isolado para testes é `postgres_executor_test`, com o mesmo usuário e senha.

Ver logs ou abrir o `psql` dentro do container:

```powershell
docker compose logs --follow postgres
docker compose exec postgres psql -U executor_dev -d postgres_executor_dev
```

Pare o ambiente sem apagar os dados:

```powershell
.\database-down.ps1
```

Para apagar o volume e recriar os dois bancos do zero:

```powershell
.\database-reset.ps1 -Confirm
```

Para personalizar as credenciais, copie `.env.example` para `.env` antes da primeira inicialização. O arquivo `.env` não é versionado.

## Compilar pelo PowerShell

Abra o **Developer PowerShell for VS** e entre nesta pasta:

```powershell
cd C:\Users\Ceolin\Documents\code\postgres-command-executer
.\build.ps1
```

O script encontra automaticamente o MSBuild instalado. Se a política do PowerShell impedir scripts locais, execute uma vez nesta janela:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

O executável será criado em:

```text
bin\Release\net48\PostgresCommandExecuter.exe
```

## Executar pelo terminal

```powershell
.\run.ps1
```

Para compilar e iniciar em um único comando:

```powershell
.\run.ps1 -Build
```

## Gerar uma versão distribuível

A versão oficial fica no campo `<Version>` de `PostgresCommandExecuter.csproj`. Para compilar e criar o ZIP:

```powershell
.\release.ps1
```

Os artefatos são gravados em `artifacts\releases`:

```text
PostgresCommandExecuter-v0.1.0-win-net48.zip
PostgresCommandExecuter-v0.1.0-win-net48.zip.sha256
```

É possível gerar outra versão sem editar o projeto:

```powershell
.\release.ps1 -Version 0.2.0
```

O build ocorre em uma pasta isolada, portanto pode ser executado mesmo com o aplicativo aberto. O ZIP contém o executável, DLLs necessárias, README e `VERSION.txt`; arquivos de depuração não são distribuídos.

O workflow `.github/workflows/release.yml` executa o mesmo pipeline manualmente ou ao enviar uma tag como `v0.2.0`, publicando ZIP e SHA-256 como artefatos da execução.

## Alternativa com `dotnet` em Windows moderno

O SDK atual não deve ser instalado no Windows Server 2012. Em uma máquina moderna com o .NET SDK e o Developer Pack 4.8 instalados:

```powershell
dotnet restore
dotnet build --configuration Release
.\bin\Release\net48\PostgresCommandExecuter.exe
```

## Uso

1. Confirme a porta, banco e usuário.
2. Digite a senha (ela não é salva).
3. Clique em **Testar conexão**.
4. Escreva a consulta e pressione `Ctrl+Enter`.
5. Use as abas **Resultado**, **JSON** e **Mensagens**.

O PostgreSQL deve estar acessível em `127.0.0.1`. O host não pode ser alterado pela interface e é novamente fixado ao montar a conexão.

> Atenção: os instaladores oficiais atuais do PostgreSQL 18 não são certificados para Windows Server 2012. Este cliente pode rodar no Server 2012, mas a instalação local do servidor PostgreSQL 18 continua sendo uma combinação sem suporte oficial.
