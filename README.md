## v1.0.1 - Vínculo opcional pelo SelfHost

- Adicionada a opção **Origem do vínculo: Softcomshop / SelfHost** para Smart PDV, Pré-Venda, Minimercado e Totem.
- Smart Comanda e Smart Autopagamento continuam obrigatoriamente pelo SelfHost.
- Smart TEF mantém o fluxo próprio e não usa essa opção.
- Ao vincular um módulo comum pelo SelfHost, o dispositivo pode trocar de módulo posteriormente no próprio Smart, inclusive para Comanda, sem refazer o vínculo.

## v1.0.0 - Primeira versao distribuida

Esta e a primeira versao oficial destinada a distribuicao para a equipe. Ela consolida os fluxos ja validados do Provisioner, incluindo SelfHost, Docker isolado, interface compacta e o mecanismo de atualizacao automatica.

### Atualizacao automatica

O Provisioner possui mecanismo de distribuicao e atualizacao automatica.

### Como funciona

1. O Provisioner consulta um `latest.json` remoto no inicio.
2. Se a versao remota for superior, baixa o ZIP da release.
3. Se `sha256` estiver preenchido, valida o pacote antes de instalar.
4. Copia o `SoftcomSmartProvisioner.Updater.exe` para `%LOCALAPPDATA%\Softcom\SmartProvisioner\Updater`.
5. Fecha o Provisioner.
6. O Updater aguarda o processo principal encerrar, fecha apenas `adb/scrcpy` executados a partir da instalacao do Provisioner, substitui os arquivos e abre o aplicativo novamente.
7. Configuracoes, sessao WebView2, logs e segredos permanecem em `%LOCALAPPDATA%\Softcom\SmartProvisioner` e nao sao apagados pela atualizacao.

### Canais

- `stable`: canal Estavel da equipe.
- `beta`: canal de Teste.

Em **Configuracoes > Atualizacao automatica** e possivel definir as URLs dos dois manifestos, escolher o canal, ativar/desativar a verificacao ao iniciar e a instalacao automatica.

Para que os colegas ja recebam a configuracao sem preencher nada, edite antes da publicacao:

`src\SoftcomSmartProvisioner\update-defaults.json`

Exemplo:

```json
{
  "stableManifestUrl": "https://SEU_HOST/stable/latest.json",
  "betaManifestUrl": "https://SEU_HOST/beta/latest.json"
}
```

### Gerar uma release

1. Execute `PUBLICAR-WINDOWS.bat`.
2. Execute `GERAR-RELEASE.bat`.
3. O pacote sera criado em `release\v<VERSAO>`.
4. Publique o ZIP e o `latest.json` em um endereco HTTP/HTTPS acessivel aos computadores da equipe.
5. O campo `downloadUrl` do `latest.json` deve apontar para o ZIP publicado.

Formato do manifesto:

```json
{
  "version": "1.0.0",
  "downloadUrl": "https://.../Softcom-Smart-Provisioner-v1.0.0.zip",
  "sha256": "...",
  "required": false,
  "notes": "Correcoes e melhorias da versao."
}
```

> `required` fica disponivel no manifesto para indicar atualizacoes obrigatorias. A instalacao automatica e controlada pela opcao local `Instalar automaticamente quando houver nova versao`.

## v0.4.1 - Modo Online sem VPN / sem banco

Esta versao adiciona o novo modo **Online**, definido como padrao para uso da equipe. Ele reutiliza a sessao autenticada do proprio Softcomshop e os endpoints WEB ja existentes, sem criar API nova e sem acessar MySQL/OpenVPN.

### Fluxo Online

1. Informar somente o cliente, por exemplo `balerion`.
2. Clicar em **Conectar**.
3. Se a sessao estiver expirada, o Provisioner abre uma janela do proprio Softcomshop para o usuario fazer login normalmente. O perfil WebView2 e persistido localmente, portanto a sessao pode ser reutilizada nas proximas execucoes.
4. A lista de empresas e obtida por `GET /cadastro/empresa`.
5. Os dispositivos sao obtidos por `GET /softauth?empresa_id={id}`.
6. **Criar dispositivo** usa `/softauth/device/novo` + `/softauth/device/salvar`; `client_id` e `client_secret` continuam sendo gerados pelo proprio Softcomshop.
7. **Desvincular** usa `/softauth/device/desvincular`.
8. **Series NFC-e/NF-e** usam `/serie-dispositivo/nfenfce/novo` e `/serie-dispositivo/nfenfce/salvar`.
9. A URL de vinculo e obtida de `/softauth/device/anexar`; nao e necessario abrir/copiar QR Code.
10. O ADB executa o fluxo normal do Smart e o vinculo final e validado novamente pela pagina de dispositivos do Softcomshop.

### Modos de acesso

- **Online · sem VPN**: padrao e recomendado para a equipe. Nao exige credenciais do banco nem OpenVPN.
- **Banco direto · avancado**: mantem o fluxo legado AWS/VPN/MySQL para diagnostico, parametrizacao e fallback. Em uma proxima etapa esse modo podera ser isolado com Docker.
- **Smart TEF**: continua usando o fluxo proprio RedeFlex e nao depende de dispositivo Softcomshop/URL.

### Seguranca

O modo Online nao grava usuario/senha do Softcomshop no codigo. A autenticacao acontece na pagina real do cliente dentro de um WebView2 com perfil local em `%LOCALAPPDATA%\Softcom\SmartProvisioner\WebView2`. As requisicoes reaproveitam apenas os cookies da sessao autenticada e o CSRF retornado pelo proprio site.

## v0.3.0

- A seção **Séries do dispositivo** agora é expansível e inicia recolhida para reduzir a rolagem da tela principal.
- Quando recolhida, exibe um resumo compacto das séries NFC-e e NF-e vinculadas.
- Clique no cabeçalho **Séries do dispositivo** para abrir ou fechar os campos.
- O botão **Atualizar** continua disponível sem precisar expandir a seção.
- Mantidas as funcionalidades da v0.2.9 para criação de dispositivo, séries fiscais e provisionamento Smart/Smart TEF.

## v0.2.9

- Smart TEF: depois de **Confirmar Configuracao**, o Provisioner aguarda a validacao das chaves e aciona automaticamente **Concluir** quando aparecer **Chaves verificadas com sucesso!**.
- O clique em **Concluir** usa a arvore de acessibilidade quando disponivel e possui fallback calibrado para o Positivo L400 quando o `UiTestAutomationBridge` nao retorna root node.
- **Criar novo dispositivo** foi habilitado no fluxo padrao. O cadastro e criado diretamente em `oauth_clients` para a empresa selecionada e passa a ser selecionado automaticamente.
- Foi adicionada a area **Series do dispositivo**, com leitura e manutencao das series **NFC-e** e **NF-e** vinculadas ao `oauth_client_id`.
- Para cada serie e possivel selecionar um cadastro existente ou criar um novo, alterar **Serie**, **Numero inicial** e **Ambiente** (Homologacao/Producao).
- As operacoes de criacao/serie validam o schema real antes da gravacao e usam a VPN automaticamente apenas quando o banco nao estiver acessivel.

## v0.2.8

- Corrige o fluxo do Smart TEF no Positivo L400: o campo **Nome do dispositivo** passa a usar um perfil calibrado para 720x1600, em vez da escala simples do XML 1080x2400.
- O preenchimento dos campos do TEF nao depende mais de o teclado virtual ser reportado como visivel. Em maquinetas/POS, o EditText pode receber eventos ADB sem o IME aparecer.
- A selecao de **Smart TEF** e o clique em **Avancar** usam a mesma captura da tela, evitando uma segunda espera do UIAutomator apos marcar o modulo.
- Mantem o UIAutomator apenas como validacao auxiliar; quando o aparelho retorna `null root node`, a preparacao nao fica parada procurando uma tela que ja esta aberta.
- Perfil L400 utilizado no fluxo TEF: Nome do dispositivo, Digitar dados manualmente, CNPJ, Empresa ID, Token e Confirmar Configuracao com pontos proprios para 720x1600.

## v0.2.7
### Correção do Smart TEF em aparelhos físicos

- O fluxo do Smart TEF não fica mais aguardando o UIAutomator reconhecer a tela `Configurar Smart TEF` após clicar em `Avançar`.
- Em alguns aparelhos físicos o Android já exibe `Nome do dispositivo`, mas o `uiautomator dump` retorna `ERROR: null root node returned by UiTestAutomationBridge` durante vários segundos/minutos.
- Após selecionar `Smart TEF` e clicar em `Avançar`, o Provisioner aguarda apenas a transição visual e passa a preencher o fluxo conhecido diretamente.
- Os pontos da tela são escalados com base na resolução retornada por `wm size`, usando como referência o XML real de 1080x2400.
- Fluxo direto: Nome do dispositivo -> Digitar dados manualmente -> CNPJ -> Empresa ID -> Token -> Confirmar Configuração.
- A validação final da tela de login continua existindo, mas é rápida e não bloqueia o processo se o UiTestAutomationBridge do aparelho não fornecer a árvore de acessibilidade.
- O timeout do `uiautomator dump` agora pode ser reduzido nas validações rápidas.

# Softcom Smart Provisioner
## v0.2.5
### Ajustes v0.2.5

- Corrige a leitura de telas do Smart TEF em aparelhos fisicos: cada `uiautomator dump` agora usa um arquivo remoto unico e possui tentativas proprias, evitando reaproveitar XML antigo durante a transicao entre telas.
- A tela `Configurar Smart TEF` passa a ser reconhecida pelo estado real dos componentes (`Nome do dispositivo`, `Digitar dados manualmente` ou campos manuais), sem depender obrigatoriamente do titulo da tela.
- O campo `Nome do dispositivo` possui fallback seguro para o primeiro `EditText` da tela de configuracao quando o Compose omitir temporariamente o rotulo na arvore de acessibilidade.
- A expansao manual aceita os tres campos obrigatorios `CNPJ`, `Empresa ID` e `Token` mesmo quando o Compose nao mantiver os quatro `EditText` simultaneamente na arvore.
- O progresso agora muda para `Tela de configuracao identificada` assim que a tela do Smart TEF for realmente reconhecida.

- Smart TEF tratado como fluxo em estados: tela inicial, seleção de módulo, tela recolhida, expansão manual e tela final.
- Após preencher Nome do dispositivo, o Provisioner clica em **Digitar dados manualmente** e obrigatoriamente captura uma nova árvore UI antes de buscar CNPJ, Empresa ID e Token.
- O sucesso do Smart TEF agora é confirmado pela tela **Seja bem vindo! / Faça login para acessar o Smart**.
- Se o campo já estiver com o valor desejado, o Provisioner não tenta limpá-lo novamente.

- Novo ícone exclusivo do Smart Provisioner aplicado ao executável/janela e à marca lateral da interface.
- Corrigido o preenchimento do Smart TEF em aparelhos onde a limpeza do campo falhava antes de digitar.
- Campos vazios não são mais submetidos a uma limpeza desnecessária.
- Quando existe valor anterior, a limpeza usa keyevents ADB simples em lotes, sem `while` no shell Android.


- Corrigido o fluxo de VPN durante a verificacao de vinculos: o acesso ao AWS e garantido antes de consultar/desvincular `oauth_clients`, e a VPN so e encerrada depois dessa etapa, imediatamente antes da configuracao do Smart.

- Smart TEF agora percorre o fluxo completo: **Iniciar Configuração → Smart TEF → Avançar → Configurar Smart TEF** antes de preencher Nome do dispositivo, CNPJ, Empresa ID e Token.
- Mantém suporte ao cenário em que o aplicativo já esteja diretamente na tela **Configurar Smart TEF**.


## v0.2.0

Ajustes da v0.2.0:

- Smart TEF habilitado com fluxo especifico do aplicativo RedeFlex.
- Package esperado do Smart TEF: `softcom.mobile.smart2.redeflex`.
- Ao selecionar Smart TEF, a interface exibe Nome do dispositivo, CNPJ, Empresa ID e Token.
- Valores padrao de teste preenchidos na tela e editaveis antes da preparacao.
- Smart TEF nao exige selecao de `oauth_clients` e nao utiliza URL de vinculo neste fluxo.
- A VPN e encerrada antes da configuracao do Smart TEF e permanece desligada ao final.
- O Provisioner abre `Digitar dados manualmente`, preenche os tres campos e aciona `Confirmar Configuracao`.
- O primeiro teste ainda deve validar a tela apresentada apos `Confirmar Configuracao`; nenhuma regra posterior foi presumida.

## v0.1.9

Ajustes da v0.1.9:

- removida a barra superior Cliente > Empresa > Dispositivo > Android, pois o fluxo ja e conduzido pelos paineis da tela;
- ao selecionar AWS 2, o cliente `jormungandr` e preenchido por padrao;
- a VPN passa a ser usada somente quando necessaria para acesso ao banco; durante o vinculo do Smart ela e desligada, pode ser ligada temporariamente para validar o banco e e desligada novamente ao final;
- o Provisioner nao deixa a VPN ativa depois da preparacao, inclusive em cenarios de erro;
- Smart TEF ficou temporariamente indisponivel na selecao automatica ate o fluxo especifico ser mapeado.

## v0.1.8

Ajustes da v0.1.8:

- o Provisioner passa a encerrar `openvpn.exe` mesmo quando a VPN foi iniciada manualmente, pelo Recuperador ou por outro processo;
- a preparacao nao continua silenciosamente enquanto ainda existir um processo OpenVPN ativo;
- o Smart permanece com a VPN ativa somente ate chegar na tela de configuracao e revelar o `Device ID`;
- antes de informar a URL, o programa pesquisa `oauth_clients` pelo `Device ID` real exibido pelo Smart;
- se o mesmo Device ID estiver vinculado a outro cadastro da empresa, o vinculo anterior e removido automaticamente;
- se o cadastro Softcomshop escolhido ja possuir outro `device_id`, ele tambem e liberado para reutilizacao;
- ao desvincular pelo banco, o `device_id` atual e preservado em `previous_device_id` quando a coluna existe, o `device_id` e limpo e `updated_at` e atualizado quando disponivel;
- somente depois da verificacao/desvinculacao do banco a VPN e desligada e a URL e confirmada no Smart;
- apos o vinculo, a VPN e restaurada e o novo `device_id` e validado no banco.

- correcao da captura XML do UIAutomator (dump e leitura executados em duas etapas);
- automacao da tela `Iniciar Configuracao`;
- selecao automatica do modulo escolhido no Provisioner;
- clique automatico em `Avancar`;
- preenchimento do campo `Digite a URL`;
- clique automatico em `Confirmar`;
- leitura do `Device ID` exibido pelo proprio Smart;
- validacao do vinculo no `oauth_clients` usando o Device ID do Smart, e nao o `secure android_id` do ADB.

## v0.1.3

Ajustes da v0.1.3:

- substituído o `datalist` nativo do campo Cliente por uma lista visual própria, mais limpa e consistente com o restante da interface;
- a lista exibe somente o nome do cliente, sem `softcoms_softcomshop_`;
- filtro em tempo real conforme a digitação, com suporte a setas, Enter e clique;
- corrigido o botão **Validar preparação**, que agora executa uma validação explícita, atualiza o card de resultado, gera a URL de vínculo e apresenta confirmação visual.


Ajustes da segunda entrega de teste:

- o acesso padrão de banco/VPN/API utilizado pelo Recuperador foi incorporado de forma cifrada ao aplicativo, sem exigir importação do arquivo Python para o uso normal;
- continua disponível a opção de sobrescrever as credenciais do banco localmente em **Configurações**;
- o cliente pode ser digitado diretamente, sem precisar carregar toda a lista de bancos;
- ao digitar `balerion`, por exemplo, o aplicativo utiliza internamente `softcoms_softcomshop_balerion`;
- na interface é exibido somente o nome do cliente, sem o prefixo `softcoms_softcomshop_`;
- a busca de bancos continua disponível apenas como apoio/autocomplete;
- a última seleção de ambiente, banco e empresa passa a ser persistida após conexão válida.


Protótipo inicial em **C# / .NET 8**, criado para automatizar a preparação de dispositivos do **Softcom Smart** em cenários de QA.

## Decisão de interface

Esta primeira versão usa:

- **.NET 8**
- **WinForms como host**
- **WebView2 para a interface**
- HTML/CSS/JavaScript local, sem servidor e sem acesso externo para a interface
- **MySqlConnector** para os bancos Softcomshop
- `adb.exe` e `scrcpy.exe` reaproveitados do Softcom Mirror Hub

A escolha de WebView2 foi feita para reaproveitar a arquitetura já validada no Softcom Mirror Hub e permitir uma interface mais clean/moderna sem misturar a lógica de banco e ADB com o front-end.

## O que já funciona nesta fase

1. Seleção entre **AWS 1** e **AWS 2**.
2. Importação das credenciais já usadas no Recuperador de Vendas.
3. Proteção local dessas credenciais pelo usuário do Windows (DPAPI).
4. Listagem dos bancos `softcoms_softcomshop_*`.
5. Montagem automática do link `https://<cliente>.meusoftcom.com.br`.
6. Leitura da tabela `empresa`.
7. Seleção da empresa.
8. Leitura da tabela `oauth_clients`.
9. Exibição apenas de cadastros ativos/visíveis quando os campos existem.
10. Listagem dos dispositivos ADB.
11. Leitura de:
   - serial ADB;
   - modelo;
   - transporte;
   - versão Android;
   - bateria;
   - `secure android_id`.
12. Comparação automática:
   - `oauth_clients.device_id`
   - `settings get secure android_id`
13. Abertura do Android selecionado no `scrcpy`.
14. Geração da URL de vínculo no formato:
   `https://<cliente>.meusoftcom.com.br/softauth/device/add?...`
15. Detecção de packages Android contendo `softcom` ou `smart`.
16. Limpeza do aplicativo com `pm clear`, depois que o package correto for validado.
17. Tentativa de reutilizar o perfil VPN já configurado pelo Recuperador de Vendas.
18. Possibilidade de selecionar manualmente outro `.ovpn`.

## Gravacoes atualmente suportadas

A versao 0.2.9 ja possui operacoes controladas diretamente no banco para:

- criar novo cadastro em `oauth_clients`;
- liberar/desvincular `device_id` durante o provisionamento;
- consultar, criar e alterar series de dispositivo em `nfce_serie` e `nfe_serie`.

No modo **Banco direto**, as gravacoes continuam validando o schema antes de executar. No modo **Online**, criacao de dispositivo, desvinculacao e series passam pelos endpoints WEB existentes do Softcomshop, reproduzindo o fluxo administrativo sem acesso direto ao banco.

## Módulos desta fase

Sem banco de mesas:

- Smart PDV
- Smart Pré-Venda
- Smart TEF
- Smart Minimercado
- Smart Totem

Aguardando a próxima fase:

- Smart Comanda
- Smart Autopagamento

Esses dois serão tratados com Selfhost/banco de mesas.

---

# Como testar

## 1. Requisitos

No Windows:

- .NET 8 SDK
- Microsoft Edge WebView2 Runtime
- OpenVPN, somente se a rede exigir
- Android Studio/emulador ou aparelho com ADB

Os binários ADB e scrcpy já estão na pasta `tools`.

## 2. Importar as credenciais existentes

Abra:

**Configurações > Importar do Recuperador**

Selecione o arquivo:

`configuracao_segredos_build.py`

do projeto `recuperador_vendas_pdv`.

O programa lê somente:

- `DATABASE_USERNAME`
- `DATABASE_PASSWORD`
- `VPN_USERNAME`
- `VPN_PASSWORD`
- `API_CLIENT_ID`
- `API_CLIENT_SECRET`

Os valores são gravados em `%LOCALAPPDATA%\Softcom\SmartProvisioner\secrets.dat` protegidos pelo usuário atual do Windows.

Eles não são exibidos na tela.

## 3. Testar o banco

Na tela **Provisionar**:

1. Selecione AWS 1 ou AWS 2.
2. Clique em **Buscar**.
3. Se o host não estiver acessível, clique em **Conectar VPN**.
4. Selecione o cliente.
5. Selecione a empresa.
6. Confira os dispositivos retornados de `oauth_clients`.

## 4. Testar ADB

Abra um emulador Android e clique em **Atualizar**.

Confirme principalmente:

- serial;
- Android;
- modelo;
- Android ID.

Precisamos validar se o Android ID exibido pelo programa é o mesmo valor que o Smart mostra como `Device ID`.

## 5. Testar um vínculo existente

Selecione:

1. cliente;
2. empresa;
3. dispositivo Softcomshop;
4. Android.

Clique em **Validar preparação**.

Resultados possíveis:

- **Vínculo confirmado**: `oauth_clients.device_id == android_id`;
- **Cadastro disponível**: o cadastro WEB está sem `device_id`;
- **Vinculado a outro Android**: os IDs são diferentes.

## 6. Testar a URL

Clique em **Gerar URL**.

Compare com a URL exibida pelo botão **QR Code** do Softcomshop.

Esse teste é importante antes de automatizarmos a inclusão da URL dentro do Smart.

---

# Próximas informações necessárias

Depois desta versão rodar, os próximos dados que precisamos capturar no F12/Network do Softcomshop são:

1. `Novo Cadastro > Salvar`
2. `Configurar Série > Salvar`
3. `Desvincular Dispositivo`

Preferencialmente usar **Copy as cURL**.

Também precisamos confirmar o package name real do Smart. A própria versão 0.1.0 possui o botão **Detectar no Android selecionado** para ajudar.

---

# Build

Para executar em desenvolvimento:

`EXECUTAR.bat`

Para publicar:

`PUBLICAR-WINDOWS.bat`

O publish é gerado em:

`publish\win-x64`


## v0.1.3 - Vinculo automatico pelo ADB

**Validar** continua sendo apenas uma conferencia. O botao **Preparar Smart** executa o fluxo: identifica o package do Smart, opcionalmente limpa os dados locais, abre o APK, procura **Novo dispositivo**, preenche a URL pelo ADB, confirma a tela e consulta `oauth_clients` por ate 15 segundos para validar o `device_id` contra o `secure android_id`.

Se algum componente da tela nao for reconhecido, o programa retorna os textos acessiveis encontrados para facilitar o ajuste do proximo build.

## v0.1.8

- Ao clicar em **Buscar bancos**, o programa verifica o acesso ao AWS e conecta a VPN automaticamente quando necessario.
- OpenVPN e taskkill sao iniciados ocultos; diagnosticos passam a ser registrados na aba **Logs**.
- Nova aba **Logs** com eventos de VPN, banco e preparacao do Smart.
- Quando o Smart exibe **Atualizacao Concluida**, o Provisioner aciona somente **Ok!!!**.
- Quando um emulador exibe **Falha na sincronizacao / Unable to resolve host** apos salvar a configuracao, o Provisioner fecha e abre o Smart novamente e segue para a validacao do vinculo no banco.


## v0.4.8 - Smart Comanda e Autopagamento via Selfhost

- Habilita os módulos **Smart Comanda** e **Smart Autopagamento**.
- Assume que o Selfhost já está instalado, configurado e com o serviço ativo.
- O Provisioner **não abre nem altera a configuração do Selfhost** nesta etapa.
- Reutiliza o cadastro de dispositivo do Softcomshop e gera a URL local do Selfhost no formato `http://IP:7711/device/add?...`.
- O IP local é detectado automaticamente priorizando interfaces IPv4 privadas ativas com gateway; pode ser sobrescrito na tela.
- A URL enviada pelo ADB preserva `client_id`, empresa, CNPJ e nome do dispositivo obtidos do vínculo Softcomshop.
- A validação final continua sendo feita pelo Softcomshop, confirmando o `device_id` do Smart.

## v0.4.7

- Quando **Limpar dados do Smart antes de vincular** estiver desmarcado, o Provisioner agora executa `force-stop` e reabre o aplicativo antes de continuar o fluxo, preservando todos os dados locais.
- O mesmo comportamento foi aplicado ao Smart TEF.


## v0.4.7 - autenticação Online automática e base SelfHost

- O modo Online tenta autenticar silenciosamente no Softcomshop com o usuário de fábrica configurado para os testes internos.
- A primeira tentativa usa a senha primária; em caso de falha, tenta a senha alternativa.
- A janela de login só é exibida se as duas tentativas automáticas falharem ou se o formulário não puder ser identificado.
- As senhas não são gravadas nos logs nem no arquivo de configurações do usuário.
- Iniciada a camada `SelfHostApiService`, exclusivamente por endpoints, com autenticação `client_credentials` e consultas iniciais de empresa/configuração de restaurante.
- A configuração administrativa do SelfHost (banco, ativação de Smart Comanda, banco de mesas e serviço) permanece [VALIDAR] até identificarmos os endpoints locais correspondentes; não foi adicionada automação visual do aplicativo.


## v0.4.9 - Correcao da preparacao ADB

- Corrige o travamento em **Preparando Smart > Identificando o aplicativo Softcom Smart**.
- O fluxo padrao agora usa diretamente `softcom.mobile.smart2` (ou o package configurado), sem executar a cadeia de descoberta por ADB antes de limpar/reabrir o aplicativo.
- Smart TEF continua no fluxo separado `softcom.mobile.smart2.redeflex`.
- A alteracao vale tambem para Smart Comanda e Smart Autopagamento via Selfhost.



## v0.5.1 - Dispositivos reais do SelfHost

- Smart Comanda e Smart Autopagamento deixam de reutilizar `client_id` de dispositivo comum do Softcomshop.
- O Provisioner lê o `Config2.json` do SelfHost instalado somente para obter a configuração já existente do dispositivo raiz.
- A lista de dispositivos é consultada pelo endpoint já usado pelo SelfHost: `/softauth/api/nfenfce/nfce/dispositivo`.
- A URL local `/device/add` é gerada com o `client_id` retornado para o dispositivo SelfHost selecionado.
- Criação de novo dispositivo SelfHost permanece desabilitada nesta etapa até validação do endpoint administrativo de inclusão; use um cadastro SelfHost disponível.
- O SelfHost não é aberto nem reconfigurado pelo Provisioner.

## v0.5.0 - Fallback para falha de `pm clear` no Android 17

- Trata a excecao interna `INotificationManager.clearData`/`NullPointerException` observada em alguns emuladores Android 17.
- Quando `pm clear` falha, tenta novamente com `pm clear --user 0`.
- Se o proprio Android continuar recusando a limpeza, o Provisioner nao encerra todo o fluxo: fecha o APK e continua sem zerar os dados, registrando aviso no log.
- A automacao seguinte continua responsavel por validar a tela real do Smart.



## v0.5.3 - Desvinculação SelfHost antes do vínculo

- Smart Comanda e Smart Autopagamento agora liberam o vínculo anterior antes de enviar a URL local do SelfHost.
- Se o cadastro SelfHost selecionado já possuir `device_id`, o Provisioner solicita a desvinculação e confirma pela listagem antes de continuar.
- Depois de ler o Device ID exibido pelo Smart, o Provisioner procura outros dispositivos SelfHost usando o mesmo `device_id` e os desvincula antes do `/device/add`.
- A desvinculação usa o endpoint existente `/softauth/api/nfenfce/nfce/dispositivo/desvincular` com autenticação do dispositivo raiz do SelfHost.
- O processo só continua quando a API confirma que o `device_id` foi liberado; caso contrário, interrompe com mensagem de validação.

## v0.5.2 - Correção de build

- Corrigido conflito de escopo CS0136 no fluxo SelfHost (`expectedDeviceId`, `refreshed` e `linked`).
- O `EXECUTAR.bat` tenta desbloquear o script de encerramento de processos antes de executá-lo, evitando o aviso de segurança do PowerShell em arquivos extraídos de ZIP baixado.

## v0.5.4 - Docker isolado e interface compacta

- Novo modo **Docker isolado** para o acesso direto ao banco.
- O OpenVPN roda dentro de um container Linux com `NET_ADMIN` e `/dev/net/tun`.
- O MySQL fica publicado somente em `127.0.0.1:13306`; o Windows e o Android continuam usando a rede normal.
- O Provisioner reutiliza o mesmo perfil `.ovpn` e as mesmas credenciais VPN já cadastradas no aplicativo.
- O container e removido ao fechar o Provisioner.
- Na primeira utilização o Docker precisa baixar/montar a imagem do DB Bridge, portanto pode levar mais tempo.
- Mantido o modo **VPN local** como fallback.
- A tela principal foi compactada: topbar, cards, campos, previews, lista ADB e painel de preparação ocupam menos altura para reduzir a rolagem.

### Requisitos do modo Docker isolado

1. Docker Desktop instalado e em execucao, usando containers Linux.
2. Perfil OpenVPN selecionado em Configuracoes.
3. Credenciais da VPN e do MySQL ja cadastradas/importadas no Provisioner.
4. Acesso à Internet na primeira montagem da imagem (Alpine/OpenVPN/socat).

## Correcao de responsividade da v1.0.0

A versao permanece **1.0.0**. Foi ajustada a inicializacao para que a descoberta ADB nao bloqueie a thread principal da janela. Tambem foi removida a espera sincrona pelo Docker no fechamento da aplicacao. Isso evita o comportamento em que a janela abria aparentemente travada, com somente a navegacao lateral do WebView respondendo.

## Ajuste Docker - v1.0.0

Correção interna mantendo a versão 1.0.0:
- o DB Bridge só é considerado pronto após validar o acesso ao host MySQL de dentro do container;
- o container não é removido automaticamente em caso de falha, permitindo consultar os logs;
- os logs agora registram DNS, rota e falha de acesso ao banco remoto;
- a mensagem da interface diferencia falha do Docker isolado de falha da VPN local.

### Ajuste Docker - v1.0.0
O modo Docker isolado agora prepara um perfil OpenVPN autocontido antes de iniciar o container. Arquivos externos referenciados pelo `.ovpn` (`ca`, `cert`, `key`, `pkcs12`, `tls-auth`, `tls-crypt`, `tls-crypt-v2` e `crl-verify`) são localizados, copiados para a área temporária do DB Bridge e as referências do perfil são reescritas para caminhos internos do container. A versão permanece 1.0.0.

### Correcao de build local - v1.0.0
Os scripts de build/publicacao encerram automaticamente instancias do `SoftcomSmartProvisioner.exe`, do Updater, ADB e scrcpy que tenham sido iniciadas a partir da pasta `bin` do proprio projeto. Isso evita `MSB3021/MSB3027` por executavel bloqueado sem encerrar instancias externas do ADB, como as usadas pelo Android Studio.

### v1.0.1 - SelfHost compacto e criação de dispositivos

- Para os módulos comuns, a origem SelfHost permanece em uma única opção compacta `Usar SelfHost`.
- Em modo SelfHost, o seletor de dispositivo ganhou o botão compacto `+ Novo`.
- A criação usa o endpoint confirmado do Selfhost.Gerenciador: `POST /softauth/api/nfenfce/nfce/dispositivo`.
- O `device_id` do cadastro filho é gerado como GUID, seguindo a chamada capturada do SelfHost.
- A série NFC-e e a numeração inicial são lidas da resposta da própria empresa antes da criação; o Provisioner não fixa uma série específica.
- Após criar, a lista é recarregada e o novo dispositivo é selecionado automaticamente.

## Ajuste v1.0.1 - criação de dispositivo SelfHost

Na criação de um novo dispositivo SelfHost, o Provisioner agora solicita explicitamente a série NFC-e e o próximo número, reproduzindo os campos observados na tela de cadastro do SelfHost. O endpoint validado recebe `serie` e `numeracao_inicial`; por isso esses valores não são mais inferidos automaticamente de outro dispositivo.

> [VALIDAR] Os campos de NF-e exibidos no SelfHost não foram enviados na captura disponível. O Provisioner não altera NF-e nesta etapa para evitar assumir um contrato não confirmado.


### v1.0.1 - criação SelfHost
- Modal compacto com NFC-e e NF-e opcional.
- NFC-e é enviada na criação do dispositivo SelfHost.
- NF-e opcional é vinculada ao mesmo `client_id` usando o fluxo fiscal já existente do Softcomshop.
- Após criar, a lista SelfHost é reconsultada com pequenas tentativas para absorver eventual atraso da API.

## Publicacao automatica no GitHub (v1.0.2+)

O repositorio esta preparado para publicar atualizacoes automaticamente usando GitHub Actions.

Fluxo recomendado:

1. Trabalhe normalmente na branch `main`.
2. Execute `PUBLICAR-ATUALIZACAO.bat`.
3. Informe a nova versao no formato `X.Y.Z` (ex.: `1.0.2`).
4. O script atualiza a versao dos projetos, envia a `main`, cria a tag `vX.Y.Z` e envia a tag ao GitHub.
5. O workflow `.github/workflows/release.yml`:
   - compila Provisioner e Updater em `win-x64` self-contained;
   - gera o ZIP da versao;
   - calcula SHA-256;
   - cria a GitHub Release;
   - anexa o ZIP;
   - atualiza `update/latest.json` na `main` com a nova versao, URL e hash.
6. Os computadores da equipe consultam sempre o manifesto fixo:

`https://raw.githubusercontent.com/SarmentoCaio/Softcom-Smart-Provisioner/refs/heads/main/update/latest.json`

O `GERAR-RELEASE.bat` continua disponivel como alternativa manual.

### Permissao do GitHub Actions

O workflow usa `contents: write`. Se a etapa de atualizar `latest.json` retornar HTTP 403, confirme em:

`GitHub > Settings > Actions > General > Workflow permissions > Read and write permissions`

### Rollback de atualizacao

Antes de substituir os arquivos, o Updater salva a instalacao anterior em:

`%LOCALAPPDATA%\SoftcomSmartProvisioner\Backups\`

Se a copia da nova versao ou o reinicio falhar, ele tenta restaurar automaticamente a versao anterior. Sao mantidos os dois backups mais recentes.


### Correcao do publicador automatico
O script de publicacao trata corretamente comandos Git sem saida (por exemplo, quando a tag ainda nao existe), evitando erro de metodo em valor nulo no PowerShell.
