# Teste v1.0.0 - Primeira versao distribuida

1. Publicar a aplicacao com `PUBLICAR-WINDOWS.bat`.
2. Confirmar que `publish\win-x64` possui `SoftcomSmartProvisioner.exe` e `SoftcomSmartProvisioner.Updater.exe`.
3. Em Configuracoes, informar uma URL de manifesto de teste e marcar `Verificar automaticamente ao iniciar`.
4. Usar um manifesto com a mesma versao instalada e validar mensagem de aplicacao atualizada.
5. Alterar o manifesto para uma versao superior e validar exibicao de `Atualizacao disponivel`.
6. Com instalacao automatica desmarcada, clicar `Atualizar agora` e confirmar: download, fechamento, substituicao dos arquivos e reabertura.
7. Confirmar que `%LOCALAPPDATA%\Softcom\SmartProvisioner` permanece com configuracoes e sessao WebView2.
8. Marcar instalacao automatica, publicar nova versao superior e reiniciar o Provisioner. Validar atualizacao sem intervencao manual.
9. Informar SHA-256 incorreto no manifesto e confirmar que a atualizacao e bloqueada.

## v0.2.5

- Smart TEF agora percorre o fluxo completo: **Iniciar Configuração → Smart TEF → Avançar → Configurar Smart TEF** antes de preencher Nome do dispositivo, CNPJ, Empresa ID e Token.
- Mantém suporte ao cenário em que o aplicativo já esteja diretamente na tela **Configurar Smart TEF**.

# Validação v0.1.1

Antes dos testes anteriores, valide:

1. Abra o aplicativo sem importar o arquivo do Recuperador. O status de **Banco AWS** deve aparecer como configurado.
2. Em **Cliente**, digite apenas o nome, por exemplo `balerion`, e clique em **Usar**.
3. Confirme que a interface não exibe `softcoms_softcomshop_`.
4. Clique em **Buscar** e confirme que as sugestões também aparecem apenas com o nome do cliente.
5. Caso queira substituir o acesso padrão, use **Configurações > Credenciais > Salvar acesso local**.

---

# Checklist de teste - Fase 1

Preencha os resultados abaixo e envie de volta para continuarmos.

## Acesso
- [ ] Aplicação abriu normalmente.
- [ ] Credenciais do Recuperador foram importadas.
- [ ] AWS 1 listou os bancos.
- [ ] AWS 2 listou os bancos, se aplicável.
- [ ] VPN foi reutilizada/conectada quando necessária.

## Empresa
- [ ] Cliente correto apareceu na lista.
- [ ] Empresas corretas foram listadas.
- [ ] Nome da empresa ficou correto.
- [ ] CNPJ ficou correto.
- [ ] Caso exista mais de uma empresa, todas apareceram.

## oauth_clients
- [ ] Dispositivos ativos foram listados.
- [ ] Cadastros excluídos não apareceram.
- [ ] `device_id` mostrado corresponde ao banco.
- [ ] Cadastro sem vínculo apareceu como disponível.

## ADB
- [ ] Emulador apareceu.
- [ ] Aparelho USB apareceu, se testado.
- [ ] Modelo correto.
- [ ] Versão Android correta.
- [ ] Android ID retornou.
- [ ] Android ID é igual ao Device ID exibido pelo Smart.
- [ ] scrcpy abriu pelo botão.

## Vínculo
- [ ] Dispositivo já vinculado ao mesmo Android foi identificado.
- [ ] Dispositivo sem vínculo foi identificado.
- [ ] Dispositivo vinculado a outro Android foi identificado.

## URL
- [ ] URL foi gerada.
- [ ] `client_id` corresponde ao dispositivo.
- [ ] `empresa_name` corresponde à empresa.
- [ ] `empresa_cnpj` corresponde à empresa.
- [ ] `device_name` corresponde ao cadastro.
- [ ] URL gerada é equivalente à exibida no QR Code do Softcomshop.

## Package Smart
- [ ] O botão Detectar retornou packages.
- [ ] Foi identificado qual é o package correto do Smart.
- [ ] `pm clear` retornou Success.
- [ ] Após limpar, o Smart voltou ao estado esperado.

## Pendências encontradas

Cole aqui mensagens, prints ou comportamentos divergentes.


## Reteste v0.1.2

1. Clique em **Buscar bancos**.
2. Digite parte do nome do cliente e confirme que a lista mostra somente o nome, sem `softcoms_softcomshop_`.
3. Selecione um cliente pela lista e confirme o carregamento das empresas.
4. Selecione empresa, dispositivo Softcomshop e Android.
5. Clique em **Validar preparação**.
6. Confirme que o botão mostra **Validando...**, o card de validação é atualizado e a URL de vínculo é exibida.


## Teste v0.1.3 - Preparar Smart

1. Selecione cliente, empresa, um cadastro `oauth_clients` disponivel e o Android.
2. Clique em **Validar** apenas se quiser conferir o relacionamento.
3. Deixe **Limpar dados do Smart antes de vincular** marcado ao trocar de configuracao.
4. Clique em **Preparar Smart**.
5. Confirme a limpeza.
6. O aplicativo deve abrir o Smart, acessar **Novo dispositivo**, informar a URL e confirmar.
7. O Provisioner consulta o banco e deve retornar **Smart vinculado** quando `oauth_clients.device_id` for igual ao Android ID selecionado.
8. Se retornar **Vinculo precisa de verificacao**, abra o scrcpy e registre a tela em que o Smart ficou.


## Teste v0.1.4 - Fluxo real do Smart

1. Selecione AWS, cliente, empresa e um cadastro `oauth_clients` disponivel.
2. Selecione o Android/emulador.
3. Selecione o modulo, inicialmente `Smart PDV`.
4. Mantenha `Limpar dados do Smart antes de vincular` marcado.
5. Clique em `Preparar Smart`.
6. O esperado e o programa executar: `Iniciar Configuracao` -> selecionar o modulo -> `Avancar` -> preencher a URL -> `Confirmar`.
7. Ao final, conferir se o `device_id` gravado em `oauth_clients` e igual ao `Device ID` exibido pelo Smart.
8. Se parar em alguma tela, abrir o scrcpy e registrar a tela e a mensagem apresentada pelo Provisioner.

Observacao: o `secure android_id` mostrado pelo ADB e mantido apenas como informacao tecnica. A validacao do vinculo usa o `Device ID` apresentado na interface do Smart.


## Teste v0.1.6 - VPN e selecao de modulo

1. Conecte a VPN e carregue cliente, empresa e dispositivo normalmente.
2. Selecione um modulo diferente de Smart PDV, por exemplo Smart Totem ou Smart TEF.
3. Clique em **Preparar Smart**.
4. O esperado e o Provisioner desativar a VPN antes de abrir/configurar o APK.
5. No Smart, confirmar que o modulo selecionado corresponde ao modulo escolhido no Provisioner.
6. Confirmar que a URL e aceita sem `Unable to resolve host`.
7. O programa deve aguardar alguns segundos e religar a VPN para validar `oauth_clients`.
8. Ao final, confirmar que o `device_id` gravado corresponde ao Device ID exibido pelo Smart.


## Teste v0.1.8 - VPN externa e reutilizacao de Device ID

1. Conecte a VPN antes de abrir ou antes de clicar em **Preparar Smart**. Pode ser uma VPN ja iniciada pelo Recuperador/fora do Provisioner.
2. Selecione cliente, empresa, modulo, cadastro Softcomshop e Android.
3. Pode utilizar um cadastro que ja esteja vinculado.
4. Clique em **Preparar Smart**.
5. Confirme que o Smart abre, seleciona o modulo e chega na tela **Configurar Smart ...**.
6. O Provisioner deve ler o `Device ID` exibido pelo Smart enquanto a VPN ainda esta ativa.
7. Se esse Device ID ja estiver em `oauth_clients`, o programa deve informar que encontrou/remover o vinculo anterior antes de continuar.
8. Depois disso, confirme que o processo `openvpn.exe` foi encerrado e que a VPN esta efetivamente desconectada.
9. O Provisioner deve preencher a URL e confirmar no Smart somente depois da desconexao.
10. O erro **O dispositivo ja encontra-se em uso para essa Empresa** nao deve mais ocorrer quando o vinculo anterior estiver em `oauth_clients`.
11. Ao final, a VPN deve ser reativada e o cadastro escolhido deve ficar com o `device_id` atual do Smart.
12. Conferir no registro antigo se `device_id` ficou vazio e, quando existir a coluna, `previous_device_id` recebeu o Device ID anterior.


## Teste v0.1.9 - fluxo simplificado e VPN sob demanda

1. Confirmar que a barra Cliente > Empresa > Dispositivo > Android nao aparece mais.
2. Selecionar AWS 2 e confirmar que o campo Cliente recebe `jormungandr` por padrao.
3. Com VPN desligada, clicar em Buscar bancos e confirmar que a VPN e acionada automaticamente quando o AWS nao estiver acessivel.
4. Preparar um Smart e acompanhar: VPN ativa para consultas do banco, desligada antes do vinculo, ligada apenas temporariamente para validacao final e desligada novamente ao terminar.
5. Confirmar nos Logs que a preparacao termina sem `openvpn.exe` ativo.
6. Confirmar que Smart TEF aparece como fluxo especifico em mapeamento e nao pode ser selecionado nesta versao.


## Teste v0.2.0 - Smart TEF

1. Selecionar **Smart TEF** no modulo.
2. Confirmar que aparece o bloco especifico com Nome do dispositivo, CNPJ, Empresa ID e Token.
3. Confirmar que o cadastro `oauth_clients` deixa de ser obrigatorio para esta preparacao.
4. Selecionar um Android que possua o package `softcom.mobile.smart2.redeflex`.
5. Manter **Limpar dados do Smart antes de vincular** marcado no primeiro teste.
6. Clicar em **Preparar Smart**.
7. Confirmar que qualquer VPN ativa e encerrada antes da configuracao.
8. Confirmar no Android que o campo Nome do dispositivo e preenchido.
9. Confirmar que **Digitar dados manualmente** e expandido.
10. Conferir o preenchimento de CNPJ, Empresa ID e Token.
11. Confirmar que o Provisioner aciona **Confirmar Configuracao**.
12. Confirmar que a tela final de login foi localizada automaticamente pelo Provisioner.

Valores padrao desta versao:

- Nome do dispositivo: `SMART 1`
- CNPJ: `16161060606116`
- Empresa ID: `106160`
- Token: configurado na tela do Provisioner conforme o valor de teste informado.


### Validar na v0.2.5

1. Confirmar que o novo ícone aparece na janela e no executável publicado.
2. Selecionar Smart TEF e preparar em um Android com a tela inicial limpa.
3. Confirmar o preenchimento automático de Nome do dispositivo, CNPJ, Empresa ID e Token.
4. Repetir com um valor já existente em Nome do dispositivo e confirmar que ele é substituído, sem erro de limpeza.


### Smart TEF - validar na v0.2.5

Fluxo esperado:
1. Bem vindo ao Smart -> Iniciar Configuração.
2. Selecionar Smart TEF -> Avançar.
3. Preencher Nome do dispositivo.
4. Clicar em Digitar dados manualmente.
5. Aguardar aparecer CNPJ, Empresa ID e Token.
6. Preencher os três campos.
7. Confirmar Configuração.
8. Considerar sucesso apenas ao localizar a tela Seja bem vindo! / Faça login para acessar o Smart.


### Correcao Smart TEF v0.2.5

Validar principalmente em aparelho fisico:

1. Limpar os dados do Smart TEF.
2. Selecionar Smart TEF no Provisioner.
3. Executar Preparar Smart.
4. Confirmar a sequencia: Iniciar Configuracao > Smart TEF > Avancar.
5. Ao aparecer `Nome do dispositivo`, o progresso deve sair imediatamente de `Avancando para Configurar Smart TEF`.
6. Confirmar o preenchimento do Nome do dispositivo.
7. Confirmar o clique em `Digitar dados manualmente`.
8. Confirmar o preenchimento de CNPJ, Empresa ID e Token.
9. Confirmar `Confirmar Configuracao`.
10. Confirmar a chegada a tela de login.

Se houver falha, copiar a mensagem e o resumo da tela exibidos no log; a v0.2.5 nao reutiliza mais o XML anterior do UIAutomator.

## v0.2.7 - Smart TEF / aparelho físico

Validar principalmente no dispositivo em que a v0.2.5 registrava:

`ERROR: null root node returned by UiTestAutomationBridge.`

Resultado esperado:

1. Abrir Smart TEF.
2. Clicar em Iniciar Configuração.
3. Selecionar Smart TEF.
4. Clicar em Avançar.
5. Não permanecer procurando a tela `Configurar Smart TEF` via UIAutomator.
6. Preencher `Nome do dispositivo` automaticamente.
7. Abrir `Digitar dados manualmente`.
8. Preencher CNPJ, Empresa ID e Token.
9. Acionar `Confirmar Configuração`.
10. Se o UIAutomator estiver disponível, reconhecer a tela de login; se não estiver, finalizar sem aguardar minutos pela árvore de acessibilidade.


## v0.2.8 - Smart TEF / Nome do dispositivo

1. Selecionar um Positivo L400 720x1600.
2. Selecionar Smart TEF e manter Limpar dados marcado.
3. Clicar em Preparar Smart.
4. Confirmar no log a linha `Layout TEF: Positivo L400 720x1600`.
5. Verificar se `SMART 1` e digitado no campo Nome do dispositivo.
6. Verificar a abertura de Digitar dados manualmente.
7. Conferir CNPJ, Empresa ID e Token.
8. Confirmar que o fluxo aciona Confirmar Configuracao sem navegar para tras.


## v0.2.9 - Conclusao do Smart TEF e administracao do dispositivo

### Smart TEF
1. Executar o fluxo completo no Positivo L400.
2. Confirmar o preenchimento de Nome do dispositivo, CNPJ, Empresa ID e Token.
3. Confirmar que `Confirmar Configuracao` e acionado.
4. Aguardar o modal `Validando configuracao`.
5. Confirmar que, ao aparecer `Chaves verificadas com sucesso!`, o Provisioner aciona `Concluir` automaticamente.
6. Confirmar a chegada a tela de login.

### Criar dispositivo
1. Selecionar cliente e empresa.
2. Marcar `Criar novo`.
3. Informar um nome de teste e criar.
4. Confirmar que o novo cadastro aparece em Dispositivo Softcomshop e fica selecionado.
5. Confirmar que o cadastro esta sem `device_id` antes do primeiro vinculo.

### Series do dispositivo
1. Selecionar um dispositivo Softcomshop.
2. Confirmar o carregamento das series NFC-e/NF-e vinculadas.
3. Alterar Serie, Numero inicial e Ambiente de uma serie de teste e salvar.
4. Recarregar e confirmar a persistencia.
5. Selecionar `Nova serie`, informar os dados e salvar.
6. Confirmar que a nova serie fica vinculada ao mesmo `oauth_client_id`.


## v0.3.0 - Layout compacto

1. Selecione um dispositivo Softcomshop existente.
2. Confirme que **Séries do dispositivo** aparece recolhido por padrão.
3. Confira o resumo de NFC-e e NF-e no cabeçalho.
4. Clique no cabeçalho e confirme que os campos são expandidos.
5. Clique novamente e confirme que a seção é recolhida sem perder a seleção.
6. Use **Atualizar** com a seção recolhida e confira se o resumo é atualizado.


## v0.4.1 - Modo Online sem VPN

### Conexao e sessao
1. Abrir o Provisioner e confirmar que **Online · sem VPN** esta selecionado por padrao.
2. Informar `balerion` e clicar em **Conectar**.
3. Sem sessao valida, confirmar que abre a janela **Entrar no Softcomshop**.
4. Realizar o login normal do Softcomshop e confirmar que a janela fecha apos a autenticacao.
5. Confirmar que nenhuma sessao OpenVPN e criada pelo modo Online.
6. Fechar e abrir o Provisioner e validar a reutilizacao da sessao enquanto ela continuar valida.

### Empresas
1. Conectar ao cliente pelo modo Online.
2. Confirmar que as empresas sao carregadas sem MySQL.
3. Conferir ID, nome e CNPJ com a tela `/cadastro/empresa` do Softcomshop.
4. Em cliente com mais de uma empresa, selecionar empresas diferentes e confirmar a troca da lista de dispositivos.

### Dispositivos
1. Selecionar uma empresa.
2. Confirmar que a lista de dispositivos e carregada pela sessao WEB.
3. Conferir dispositivos vinculados e disponiveis com a tela do Softcomshop.
4. Selecionar **Criar novo**, informar um nome e criar.
5. Confirmar que o cadastro aparece na lista e fica selecionado, inicialmente sem `device_id`.
6. Repetir o clique apenas uma vez e confirmar que o Provisioner nao faz POST duplicado.

### Series
1. Selecionar um dispositivo e expandir **Series do dispositivo**.
2. Conferir NFC-e e NF-e existentes.
3. Criar uma nova serie em Homologacao e validar a persistencia apos Atualizar.
4. Alterar numero inicial/ambiente de uma serie de teste e confirmar o valor reaberto pelo Softcomshop.
5. Repetir para NFC-e e NF-e.

### Provisionamento completo Online
1. Selecionar cliente, empresa, dispositivo, modulo e Android.
2. Clicar em **Preparar Smart**.
3. Se o Device ID ja estiver vinculado, confirmar a desvinculacao pelo Softcomshop.
4. Confirmar que a URL e obtida sem abrir QR Code.
5. Confirmar que o Android mantem acesso normal a Internet durante todo o processo.
6. Confirmar o fluxo ADB: limpar Smart -> Iniciar Configuracao -> selecionar modulo -> Avancar -> informar URL -> Confirmar.
7. Ao finalizar, confirmar o novo `device_id` na lista do Softcomshop.
8. Confirmar que nenhum acesso MySQL/VPN foi necessario.

### Banco direto
1. Alternar para **Banco direto · avancado**.
2. Confirmar que AWS 1/AWS 2, Buscar bancos e Conectar VPN voltam a aparecer.
3. Validar que o fluxo legado continua funcionando sem interferencia do modo Online.

### v0.5.3 - Teste de desvinculação SelfHost

1. Selecione Smart Comanda ou Smart Autopagamento.
2. Use um dispositivo SelfHost disponível.
3. Execute o preparo com um Smart cujo Device ID esteja atualmente vinculado a outro cadastro SelfHost.
4. Validar no log a identificação do conflito e a tentativa de desvinculação antes do envio da URL.
5. Resultado esperado: o Device ID anterior é liberado e somente depois a URL `/device/add` é confirmada no Smart.


## Docker isolado - arquivos auxiliares OpenVPN
- Selecionar o mesmo perfil `.ovpn` utilizado no Windows.
- Iniciar o Docker isolado.
- Validar no log as mensagens `Arquivo auxiliar da VPN preparado para o Docker` e `Perfil OpenVPN preparado para o Docker`.
- Confirmar que não ocorre mais `Cannot pre-load keyfile` para arquivos externos como `tls-auth`/`tls-crypt`.
- Se algum arquivo referenciado não existir, o Provisioner deve informar explicitamente qual arquivo não foi localizado antes de iniciar o container.

## SelfHost - criação de dispositivo (v1.0.1)

1. Selecionar cliente e empresa.
2. Selecionar um módulo comum e marcar `Usar SelfHost`, ou selecionar Smart Comanda/Smart Autopagamento.
3. Confirmar que o card permanece compacto e mostra `Dispositivo SelfHost` com o botão `+ Novo`.
4. Clicar em `+ Novo`, informar um nome e confirmar.
5. Validar que o dispositivo é criado, a lista é atualizada e o cadastro novo fica selecionado.
6. Confirmar nos Logs a chamada de criação SelfHost e, no SelfHost, a existência do novo cadastro.
7. [VALIDAR] Confirmar que a série NFC-e utilizada corresponde à empresa configurada no SelfHost.
