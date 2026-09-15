# Validacao v1.0.3 - SelfHost 4.1+ e Smart 8.1+

Esta validacao permanece na versao **1.0.3**. Nao publicar no canal de atualizacao antes de concluir os testes abaixo.

## 1. Compilar no Windows

1. Extrair o projeto em uma pasta nova.
2. Executar `PUBLICAR-WINDOWS.bat`.
3. Confirmar que o projeto principal e o Updater compilam sem erros.
4. Abrir o Provisioner gerado e confirmar que a interface mostra a versao 1.0.3.

## 2. Regressao do SelfHost 4.0

Com um SelfHost 4.0.x instalado:

- a deteccao deve mostrar `SelfHost 4.0`;
- o fluxo deve continuar usando `Config2.json`;
- listar, criar, desvincular e gerar URL devem continuar funcionando como antes;
- a URL local deve continuar terminando em `/device/add`.

Se este fluxo mudar, interromper a publicacao: a alteracao da 4.1 nao deve quebrar a 4.0.

## 3. SelfHost 4.1+

Com a instalacao de teste 4.1+:

- a deteccao deve mostrar `SelfHost 4.1+` e a versao encontrada;
- o Provisioner nao deve procurar `Config2.json` para listar/criar/preparar o dispositivo;
- ao precisar da administracao do dispositivo, deve reutilizar a sessao do Softcomshop;
- se a sessao estiver expirada, o fluxo normal de autenticacao Web deve aparecer;
- listar dispositivos deve carregar os registros do Softcomshop da empresa selecionada;
- desvincular deve remover o `device_id` pelo Softcomshop e confirmar a lista atualizada.

### Lista e criacao no SelfHost 4.1

Nesta validacao, a lista carregada para 4.1 ainda e a lista OAuth do Softcomshop e deve ser apresentada como tal. Ela **nao deve ser tratada como lista confirmada do Gerenciador SelfHost 4.1**.

A criacao automatica em `+ Novo` fica bloqueada no SelfHost 4.1 ate mapear a autenticacao/requisicao exata usada pelo botao `+ Adicionar Dispositivo` do Gerenciador. Validar:

- ao tentar criar, deve aparecer erro dentro do modal;
- nenhum novo `oauth_client` deve ser criado;
- a lista nao deve ganhar um registro fantasma apos a tentativa;
- SelfHost 4.0 continua podendo criar pelo fluxo legado.

[VALIDAR] Capturar a requisicao do Gerenciador SelfHost 4.1 para liberar a criacao correta numa proxima revisao.

### Serie fiscal

Ao selecionar um dispositivo ja existente, validar:

- a serie atual de NFC-e/NF-e deve aparecer quando o formulario do Softcomshop retornar esse valor;
- o campo `Serie` continua editavel;
- digitando uma serie diferente e salvando, o Provisioner deve enviar a nova serie como novo vinculo, sem reaproveitar o ID da serie anterior;
- erros HTTP 422 devem aparecer dentro do painel de series, por exemplo `Serie NFCe em uso.`, e nao escondidos atras de modal/toast;
- apos erro, os valores digitados devem continuar visiveis para correcao.

## 3.1 Clientes Online e cache de lista

Validar o fluxo Online:

- autenticar um cliente uma vez;
- fechar e abrir novamente o Provisioner;
- o nome do cliente deve continuar disponivel na lista de sugestoes;
- ao digitar parte do nome, a lista deve ser filtrada em tempo real;
- a sessao Web persistente deve ser reutilizada enquanto ainda for valida; se a sessao do Softcomshop tiver expirado, uma nova autenticacao continua sendo esperada.

Com `Usar SelfHost` marcado:

- carregar os dispositivos uma vez;
- trocar somente o modulo Smart;
- nao deve ocorrer nova consulta/listagem de dispositivos;
- a selecao atual e as series carregadas devem permanecer.

## 4. URL `/device/add`

Gerar a URL de um dispositivo SelfHost 4.1+ e conferir que **nao** aparece `/api/v2/device/add`.

Para SelfHost local, o esperado e equivalente a:

```text
http://IP_DO_PC:7711/device/add?...parametros-do-vinculo...
```

Os parametros devem ser os mesmos da URL real obtida no Softcomshop.

Se for informada uma base com relay, por exemplo:

```text
https://host/relayId
```

a URL deve preservar o prefixo:

```text
https://host/relayId/device/add?...
```

Isso e importante porque o pre-request deriva o token assim:

```text
/relayId/device/add
-> /relayId/authentication/token
```

## 5. Smart legado menor que 8.1

Usar uma versao anterior a 8.1.0.0 e confirmar:

- o log classifica como `Smart legado (< 8.1)`;
- validar especialmente `8.0.0-sdk25` em Android 7.x, alem de outras versoes 8.0;
- se o APK voltar para a Home logo apos o primeiro launch, o Provisioner deve tentar abrir o Smart novamente uma unica vez;
- quando aparecer a tela antiga de login com `Empresa`, `Email ou Nome`, `Senha` e `LOGIN`, o Provisioner deve abrir a engrenagem localizada a direita do LOGIN;
- a engrenagem deve ser procurada primeiro por acessibilidade/resource-id e pela posicao relativa ao LOGIN; somente se esses dados nao existirem pode ser usado o fallback proporcional da tela;
- depois da engrenagem, a automacao deve reconhecer a tela de configuracao antes de procurar `Device ID` e o campo de URL;
- a mensagem `A tela de configuracao abriu, mas o Device ID do Smart nao foi localizado` nao pode mais ocorrer enquanto a tela real ainda for a tela de login;
- selecao de modulo, preenchimento da URL e sincronizacao continuam funcionando quando essas telas fizerem parte da versao legada testada;
- o fluxo Smart 8.1+ nao deve ser alterado por essa correcao.

## 6. Smart 8.1.0.0 ou superior

Antes do fluxo de tela, confirmar no log que `8.1.0`, `8.1.0.0` e versoes superiores sao classificadas como `Smart 8.1+`. O valor `8.1.0` nao pode cair no legado.

### 6.1 Primeira configuracao / dados do Smart limpos

Este e o fluxo que ja funcionava na v1.0.2 e deve ser preservado, inclusive para Smart Comanda e Smart Autopagamento via SelfHost.

1. Provisioner detecta `Smart 8.1+`.
2. Se aparecer `Bem vindo ao Smart`, aciona `Iniciar Configuracao`.
3. Ao aparecer `Selecione o modulo`, localiza exatamente o modulo solicitado.
4. Para Smart Comanda, seleciona `Smart Comanda`.
5. Clica em `Avancar`.
6. Confirma que a tela seguinte corresponde ao modulo solicitado.
7. Informa a URL de vinculo no campo de URL.
8. Confirma a configuracao.
9. Aguarda a sincronizacao inicial e trata sucesso/falha como no fluxo ja validado.

O log deve conter `smart81-onboarding` quando o 8.1+ estiver nesse estado. Nesse caso, o Provisioner **nao deve procurar Nova Empresa**.

### 6.2 Smart 8.1+ fora do onboarding

Quando a tela inicial nao for Bem-vindo, selecao de modulo ou configuracao do modulo solicitado, o Provisioner usa o fluxo administrativo do Smart 8.1+:

1. Abre a engrenagem/Configuracoes.
2. Localiza e abre `Nova Empresa`.
3. Localiza a empresa indicada por `empresa_name` na propria URL `/device/add`.
4. Seleciona essa empresa.
5. Clica no primeiro `Confirmar` normalmente.
6. Localiza e clica em `DIGITAR`.
7. Localiza o campo `Host`.
8. Limpa o valor existente e informa a URL `/device/add`.
9. Executa toque prolongado de **5 segundos** em `Confirmar`.
10. Aguarda a tela de revisao com `Empresa`, `Dispositivo`, `Client ID` e `Host`.
11. Clica no `Confirmar` final normalmente.
12. Aguarda `Dados sincronizados com sucesso.` e clica em `OK` quando acessivel.

### Comportamento de seguranca

A automacao escolhe o caminho pelo **estado real da tela**, nao apenas pela versao do APK. Isso evita a regressao em que um Smart 8.1+ na tela `Selecione o modulo` era enviado incorretamente para `Configuracoes > Nova Empresa`.

## 7. O que enviar se algum teste falhar

Para uma falha no Smart 8.1+, guardar:

- etapa exibida no Provisioner;
- mensagem de erro;
- trecho dos Logs desde a deteccao da versao ate a falha;
- screenshot da tela do Smart;
- se possivel, o dump da arvore de acessibilidade da tela em que parou.

Para uma falha no SelfHost 4.1+, guardar:

- versao detectada;
- operacao que falhou (listar, criar, serie, URL, desvincular ou preparar);
- mensagem e trecho dos Logs;
- formato da URL sem expor `client_secret`, token ou outras credenciais.


## 8. Correções desta rodada: Smart 8.0, pesquisa de dispositivo e clientes salvos

### 8.1 Smart 8.0 / Android 7

1. Selecionar um Android com Smart `8.0.0-sdk25` ou outra versão `< 8.1`.
2. Executar **Preparar Smart**.
3. Confirmar no log que o package usado é `softcom.mobile.smart2`.
4. Se o APK voltar para a Home, o Provisioner pode tentar reabrir somente o Smart uma vez.
5. Antes de usar qualquer fallback por posição, a árvore do UIAutomator precisa pertencer a `softcom.mobile.smart2`.
6. Se o Smart não permanecer em primeiro plano, a execução deve parar com a etapa `legacy-launch-verify`; não pode tocar em ícones da Home nem abrir outro APK.
7. Na tela antiga de login, deve localizar a engrenagem e seguir para a configuração.

### 8.2 Pesquisa de dispositivo

1. Carregar os dispositivos da empresa/SelfHost.
2. Clicar no campo **Dispositivo SelfHost/Softcomshop**.
3. Digitar parte do nome do dispositivo.
4. A lista deve filtrar em tempo real, priorizando nomes que começam pelo texto digitado.
5. Selecionar um resultado com mouse ou setas + Enter.
6. O preview, séries e ações devem usar exatamente o dispositivo selecionado.

### 8.3 Clientes salvos

1. Autenticar um cliente Online ao menos uma vez.
2. Confirmar que o nome aparece na pesquisa de clientes após reiniciar o Provisioner.
3. Abrir **Configurações > Clientes salvos**.
4. Clicar em **Excluir** no cliente desejado e confirmar.
5. O cliente deve desaparecer da lista local e das sugestões após a próxima abertura.
6. A exclusão não deve apagar empresa, dispositivo ou qualquer cadastro do Softcomshop.

## Mapeamento Smart 8.0 legado

Se o Smart 8.0 nao permanecer na tela correta ou a engrenagem nao for reconhecida:

1. Execute `MAPEAR-SMART-8.0.bat` na raiz do projeto/publicacao.
2. Informe o serial ADB solicitado.
3. Na etapa 1, deixe o Smart 8.0 na tela de login com a engrenagem visivel.
4. Na etapa 2, clique manualmente na engrenagem e deixe a tela de configuracao aberta.
5. Compacte a pasta `MAPEAMENTO-SMART80-*` gerada e envie para analise.

Nesta revisao, o fluxo legado nao faz mais `force-stop`/reabertura automatica quando a interface em primeiro plano nao pertence ao Smart. Ele interrompe preservando a tela para diagnostico.

## Ajuste adicional - Smart 8.0 / Android 7 mapeado em 14/09/2026

Mapeamento real do K2_MINI com Smart 8.0.0-sdk25:

- Package: `softcom.mobile.smart2`.
- Login: `softcom.mobile.smart.views.activities.login.LoginActivity`.
- Abertura pela engrenagem: `softcom.mobile.smart.views.activities.empresa.EmpresaActivity`.
- O `uiautomator dump` na LoginActivity retornou `ERROR: could not get idle state`; por isso o Provisioner nao deve executar UIAutomator nessa tela.
- Resolucao capturada: 1080x1920.
- Centro da engrenagem capturado: aproximadamente 1000x477; a automacao usa a proporcao equivalente apos confirmar a LoginActivity pelo `dumpsys`.
- Na EmpresaActivity o UIAutomator funcionou e confirmou:
  - texto `Softcom Smart - Configurar Empresas`;
  - `Device ID: ...`;
  - botao `NOVA EMPRESA`, resource-id `softcom.mobile.smart2:id/btn_novo`;
  - botao `VOLTAR`, resource-id `softcom.mobile.smart2:id/btn_cancelar`.

### Validar no Smart 8.0

1. Selecionar K2_MINI / Smart 8.0.0-sdk25.
2. Marcar limpeza dos dados e executar Preparar Smart.
3. Confirmar no log `legacy-settings`: LoginActivity reconhecida sem UIAutomator.
4. Confirmar `legacy-settings-tap`: toque na engrenagem mapeada.
5. Confirmar `legacy-settings-open`: EmpresaActivity aberta.
6. Confirmar que o Device ID foi lido na tela Configurar Empresas.
7. Confirmar `legacy-new-company`: botao Nova Empresa acionado.
8. Continuar o teste e registrar a primeira etapa que nao for reconhecida, caso exista.

O fluxo Smart 8.1+ nao foi alterado por esta correcao.

## Smart 8.0 - permissao de armazenamento

No K2_MINI / Android 7.1.2, o Smart 8.0 solicita a permissao de acesso a fotos, midia e arquivos ao entrar na configuracao.
O mapeamento do package confirma as permissoes `android.permission.READ_EXTERNAL_STORAGE` e `android.permission.WRITE_EXTERNAL_STORAGE`.

Validar:

1. Marcar `Limpar dados do Smart antes de vincular`.
2. Executar a preparacao no Smart 8.0.
3. Conferir no log a etapa `legacy-permissions` antes do `launch`.
4. A janela `Permitir que o app Softcom Smart 2 acesse fotos, midia e arquivos do dispositivo?` nao deve interromper o fluxo.
5. Se a ROM ainda exibir a janela, conferir a etapa `legacy-permission-dialog`; o Provisioner deve tentar conceder via ADB e, como fallback, localizar `PERMITIR` na janela do Android.
6. Depois disso deve abrir `EmpresaActivity` e continuar para `NOVA EMPRESA`.

[VALIDAR] Confirmar o comportamento real no K2_MINI, pois o build nao foi executado neste ambiente.

## Smart 8.0 - Nova Empresa obrigatoria

1. Use Smart 8.0 no K2_MINI e deixe a opcao de limpar dados habilitada.
2. Execute **Preparar Smart**.
3. Confirme que o Provisioner abre a engrenagem e chega em **Softcom Smart - Configurar Empresas**.
4. Confirme que ele aciona automaticamente **NOVA EMPRESA** (`softcom.mobile.smart2:id/btn_novo`).
5. O log deve registrar `legacy-new-company` e, depois da troca de tela, `legacy-new-company-open`.
6. O Provisioner nao deve tentar preencher a URL enquanto ainda estiver visivel **Empresas Cadastradas**.
7. Se o `resource-id` nao vier no dump, o log pode registrar `legacy-new-company-fallback`; esse fallback so pode ocorrer com a `EmpresaActivity` confirmada.
8. [VALIDAR] Caso a automacao pare apos abrir **Nova Empresa**, coletar print e log da nova tela para mapear somente a etapa seguinte.


## Smart 8.0 - fluxo completo apos o mapeamento

O fluxo esperado agora e:

1. `LoginActivity`: nao executar UIAutomator; conceder permissoes e acionar a engrenagem.
2. `EmpresaActivity`: ler Device ID e acionar **NOVA EMPRESA**.
3. `EmpresaAddConfigActivity`: selecionar exatamente o modulo escolhido no Provisioner e clicar **CONFIRMAR**.
4. Aguardar `EmpresaAddActivity`.
5. Localizar e clicar **DIGITAR**.
6. Localizar especificamente o campo **Host**.
7. Limpar o conteudo existente; a rotina faz duas passagens de exclusao para cobrir URLs antigas longas.
8. Informar a URL `/device/add` gerada pelo Provisioner.
9. Ocultar o teclado e localizar **CONFIRMAR**.
10. Manter **CONFIRMAR** pressionado por 5 segundos.
11. Aguardar a tela de revisao dentro da mesma `EmpresaAddActivity`.
12. Clicar **CONFIRMAR** novamente.
13. Aguardar sincronizacao e tratar sucesso/falha/timeout com o comportamento ja existente.

### Logs esperados

- `legacy-settings` / `legacy-settings-open`;
- `legacy-new-company` / `legacy-new-company-open`;
- `legacy80-module`;
- `legacy80-module-confirm`;
- `legacy80-type`;
- `legacy80-host`;
- `legacy80-hold-confirm`;
- `legacy80-review`;
- `legacy80-final-confirm`;
- `legacy80-sync` / `legacy80-sync-success`.

### Compilacao

Foi corrigido o `CS0136` em `SmartUiAutomationService.cs` causado pela segunda declaracao local de `display`. [VALIDAR] Executar `PUBLICAR-WINDOWS.bat` no Windows e confirmar que esse erro nao volta a ocorrer. O aviso `MSB3277` de `WindowsBase` continua sendo apenas warning no log fornecido e nao foi a causa da falha de compilacao observada.

[VALIDAR] Se o fluxo parar depois do toque prolongado, coletar apenas print/log da tela de revisao; nao e necessario repetir todo o mapeamento.

## Regressao Smart 8.0 - LoginActivity

- [ ] Ao iniciar o Smart 8.0 no Android 7, o log deve registrar `legacy-activity` com `LoginActivity` antes de qualquer leitura pelo UIAutomator.
- [ ] Enquanto o primeiro plano ainda for o launcher, o Provisioner deve aguardar; nao deve executar `uiautomator dump` nem fechar/reabrir o APK em loop.
- [ ] Com `LoginActivity` confirmada, deve executar `legacy-settings-tap` e abrir `EmpresaActivity`.
- [ ] Somente depois de `EmpresaActivity` confirmada o UIAutomator pode ser utilizado para localizar `NOVA EMPRESA`.

## Ajuste adicional - Smart 8.0 / NOVA EMPRESA sem UIAutomator

- [ ] Confirmar que o Smart 8.0 abre `LoginActivity`.
- [ ] Confirmar que a engrenagem abre `EmpresaActivity`.
- [ ] Nesta tela, confirmar no log que NAO ocorre leitura UIAutomator antes de `NOVA EMPRESA`.
- [ ] Confirmar o log `legacy-settings-open` informando que a EmpresaActivity permanecera sem UIAutomator.
- [ ] Confirmar o log `legacy-new-company` com as coordenadas mapeadas.
- [ ] Confirmar que `NOVA EMPRESA` abre `EmpresaAddConfigActivity`.
- [ ] A partir da selecao do modulo, validar o fluxo existente: modulo > Confirmar > DIGITAR > Host > Confirmar 5s > Confirmar final.
- [ ] Revalidar Smart 8.1 para garantir que o fluxo ja aprovado nao sofreu regressao.


## Smart 8.0 - Totem/AutoPagamento em dispositivo de tela grande

Este teste deve ser executado no dispositivo de autoatendimento usado para **Smart Totem** e **Smart AutoPagamento**. O fluxo de celular/GPOS permanece separado.

1. Selecionar `Smart Totem` no Provisioner e preparar o K2_MINI com Smart `8.0.0-sdk25`.
2. Confirmar `LoginActivity -> EmpresaActivity -> EmpresaAddConfigActivity`.
3. Na tela de módulos, o Provisioner não deve executar UIAutomator para Totem/AutoPagamento.
4. Confirmar no log `legacy80-large-module` e verificar que **Smart Totem** é selecionado.
5. Confirmar `legacy80-large-module-confirm`: deve ocorrer somente um toque em **CONFIRMAR**.
6. A tela seguinte deve ser `EmpresaAddActivity`; se não abrir, a execução deve parar sem pressionar Voltar e sem reiniciar o Smart.
7. Confirmar `legacy80-large-type`: aciona **DIGITAR**.
8. Confirmar `legacy80-large-host`: seleciona **Host**, limpa o conteúdo e informa a URL `/device/add`.
9. Confirmar `legacy80-large-hold-confirm`: mantém **CONFIRMAR** pressionado por aproximadamente 5 segundos.
10. Confirmar `legacy80-large-final-confirm`: executa o segundo **CONFIRMAR** uma única vez.
11. Validar a sincronização final.
12. Repetir com `Smart AutoPagamento`; a única diferença esperada na tela de seleção é a linha do módulo escolhida.
13. Revalidar um módulo de celular/GPOS para confirmar que ele não usa as coordenadas específicas do Totem/AutoPagamento.

[VALIDAR] As coordenadas foram derivadas do mapeamento visual do K2_MINI em 1080x1920 e são escaladas proporcionalmente para a resolução retornada por `wm size`.

## Ajuste final - OK apos sincronizacao no Smart 8.0

No fluxo Smart 8.0, ao detectar **Dados sincronizados com sucesso.**, o Provisioner deve agora acionar **OK** automaticamente antes de finalizar. Primeiro tenta localizar o controle pelo XML; para Smart Totem/AutoPagamento existe fallback proporcional somente se o OK nao for exposto pelo UIAutomator e a `EmpresaAddActivity` continuar em primeiro plano.

Logs esperados:

- `legacy80-sync-success`
- `legacy80-sync-ok` quando o botao for localizado pela interface; ou
- `legacy80-sync-ok-fallback` no Totem/AutoPagamento quando for necessario usar o ponto mapeado.
## 6.2 Smart 8.0 - celular/GPOS

Validar em um dispositivo vertical/celular ou GPOS com Smart 8.0.x:

- a engrenagem da LoginActivity deve ser acionada usando o perfil móvel, sem reutilizar a coordenada do Totem;
- Nova Empresa deve abrir normalmente;
- Smart PDV deve manter a seleção padrão sem desligar o switch;
- para Comanda, Pré-Venda, Minimercado e TEF, deve tocar somente no módulo solicitado;
- o primeiro Confirmar deve abrir EmpresaAddActivity;
- DIGITAR deve ser acionado, o Host deve ser limpo e a URL deve ser preenchida;
- Confirmar deve ser mantido por aproximadamente 5 segundos e depois acionado novamente;
- no Android 7 o fluxo não deve executar UIAutomator nas etapas críticas; em Android moderno, a tela final deve usar a árvore apenas para confirmar DIGITAR, Host editável e os botões Confirmar;
- Totem/AutoPagamento devem continuar usando o perfil de tela grande já validado.

Logs esperados no perfil móvel: `legacy80-mobile-module`, `legacy80-mobile-module-confirm`, `legacy80-mobile-type`, `legacy80-mobile-host`, `legacy80-mobile-hold-confirm` e `legacy80-mobile-final-confirm`.


## Ajuste adicional - Smart 8.0 celular/GPOS em Android moderno

- Em Android 7 / SDK 25, a LoginActivity continua sem UIAutomator e usa o ponto mapeado, pois o dump pode retornar `could not get idle state`.
- Em Android 8+ / SDK > 25, o Provisioner tenta localizar a engrenagem pela arvore da tela e somente usa coordenada proporcional como fallback.
- Depois do toque na engrenagem, a verificacao consulta a Activity real em primeiro plano para detectar janelas do PermissionController/PackageInstaller antes de consultar a Activity filtrada do Smart.
- Se uma permissao aparecer, o Provisioner tenta liberar pelo ADB e, se necessario, aciona o botao Permitir/Allow na propria janela.
- Validar no emulador Android 17 com Smart 8.0.1 e modulo Smart Comanda.
- Resultado esperado no log: `legacy-settings` informando SDK > 25, seguido de `legacy-settings-tap` com origem `arvore da tela` ou `resource-id`, e depois `legacy-settings-open` com EmpresaActivity.

## Smart 8.0 celular/GPOS - permissao no primeiro acesso a Configuracoes

Em Android moderno, o primeiro toque na engrenagem pode abrir a permissao de fotos/videos. O Provisioner deve:

1. aceitar a permissao quando ela aparecer;
2. aguardar o retorno para `LoginActivity`;
3. clicar novamente na engrenagem uma unica vez;
4. somente entao aguardar `EmpresaActivity`.

Logs esperados quando a permissao aparecer:

- `legacy-permission-dialog`
- `legacy-settings-retap`
- `legacy-settings-open`

O fluxo Android 7/Totem ja validado nao deve ser alterado por esta regra.

## Smart 8.0 celular/GPOS - validar DIGITAR antes do Host

- [ ] Chegar na `EmpresaAddActivity` depois de selecionar o modulo e confirmar.
- [ ] Confirmar no log `legacy80-mobile-type`: **DIGITAR** foi localizado pela interface e acionado antes de qualquer tentativa de limpar o Host.
- [ ] Confirmar `legacy80-mobile-host-focus`: o Provisioner somente continua depois que **Host** aparece como campo editável; depois toca exatamente nesse campo.
- [ ] Se a primeira tentativa nao ativar a edicao, deve aparecer `legacy80-mobile-type-retry` e ocorrer somente uma segunda tentativa de `DIGITAR > Host`.
- [ ] O log `legacy80-mobile-host` so pode aparecer depois da validacao do modo de edicao.
- [ ] Confirmar que o Host atual e apagado por completo e a URL de vinculo e inserida.
- [ ] Confirmar `legacy80-mobile-hold-confirm`: pressionamento de aproximadamente 5 segundos no primeiro **CONFIRMAR**.
- [ ] Confirmar `legacy80-mobile-final-confirm`: segundo **CONFIRMAR** com toque normal.
- [ ] Confirmar sincronizacao e clique final em **OK**.


## Smart 8.0 celular/GPOS - Android moderno: DIGITAR obrigatório

- Em SDK > 25, a tela final de configuração não usa coordenada fixa para avançar automaticamente.
- O Provisioner procura `DIGITAR` na árvore da tela e toca no controle real.
- A etapa só é considerada concluída quando `Host` aparece como `EditText`.
- Se `Host` não ficar editável, há somente uma segunda tentativa em `DIGITAR`; depois o fluxo para sem apagar/preencher nada.
- O primeiro e o segundo `CONFIRMAR` também são localizados na interface real.
- Resultado esperado: nunca deve aparecer `legacy80-mobile-host` sem antes aparecer `legacy80-mobile-type` e `legacy80-mobile-host-focus`.

## Ajuste adicional - Smart 8.0 celular/GPOS - DIGITAR mapeado

Mapeamento recebido em `MAPEAMENTO-DIGITAR-SMART80` confirmou o seguinte no emulador 1080x2400:

- Antes de `DIGITAR`, a Activity em primeiro plano e `EmpresaAddActivity`.
- Depois de clicar manualmente em `DIGITAR`, a Activity continua sendo `EmpresaAddActivity`.
- Portanto, a troca para o modo manual nao pode ser validada por mudanca de Activity.
- O estado confiavel aparece ao tocar no `Host`: o IME/teclado passa de oculto para visivel.
- Por isso, a tela final do Smart 8.0 em celular/GPOS nao usa UIAutomator para `DIGITAR`/`Host`.

Fluxo esperado:

1. `EmpresaAddActivity` aberta.
2. Tocar em `DIGITAR` pelo ponto proporcional mapeado.
3. Confirmar que `EmpresaAddActivity` permaneceu em primeiro plano.
4. Tocar em `Host` pelo ponto proporcional mapeado.
5. Somente continuar quando o teclado estiver visivel.
6. Selecionar tudo, apagar e informar a URL.
7. Ocultar o teclado.
8. Manter `CONFIRMAR` pressionado por aproximadamente 5 segundos.
9. Acionar `CONFIRMAR` novamente.
10. Aguardar sincronizacao e finalizar em `OK`.

Se o teclado nao aparecer depois de `DIGITAR -> Host`, a automacao repete essa dupla uma unica vez e interrompe o fluxo se ainda nao houver foco de edicao.

### Ajuste adicional - Smart 8.0 celular/GPOS: chegada obrigatoria ao DIGITAR
- Depois do Confirmar do modulo, Android moderno valida a `EmpresaAddActivity` pelo primeiro plano antes de executar qualquer outra etapa.
- Ao confirmar a tela final, o log deve registrar `legacy80-mobile-device-screen` e imediatamente seguir para `legacy80-mobile-type`.
- O clique em DIGITAR usa o centro medido da captura real 304x678 (~225,115), escalado para a resolucao fisica do Android.
- O fluxo nao fecha o teclado com `KEYCODE_BACK` depois de informar o Host, evitando retorno involuntario para a selecao do modulo.
- Se a `EmpresaAddActivity` nao for confirmada, o processo para antes de DIGITAR e informa a Activity detectada.
