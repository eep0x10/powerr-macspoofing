# Operação e recuperação

## Preparação

1. Confirme escopo, janela e porta do teste. Tenha acesso local ou caminho de recuperação independente da interface escolhida.
2. Rode `./PowerR.ps1 -Action Adapters` e anote o GUID da interface física.
3. Para captura, instale Wireshark com TShark/Npcap. Pode indicar o executável por `-TSharkPath`. Um TAP passivo com duas portas de monitoramento exige observar as duas direções separadamente.
4. Execute `Capture` e associe os MACs observados à topologia e ao inventário. `Packets` é quantidade de quadros vistos durante a janela, não quantidade de dispositivos.
5. Execute `Plan` e `Apply` com o mesmo adaptador/MAC, em PowerShell elevado.

`Apply` cria `%LOCALAPPDATA%\PowerR\{GUID}.json` com MAC original, override anterior, destino e horário UTC. Esse arquivo é local, não entra no Git e não é um registro de captura. Guarde-o até a restauração. O script evita sobrescrever uma sessão aberta.

## Recuperação

Execute `./PowerR.ps1 -Action Status -Adapter '<GUID>'` para ver estado e backup. Execute `./PowerR.ps1 -Action Restore -Adapter '<GUID>' -AcknowledgeDisconnect` em console elevado e local. O backup só é removido após o MAC original reaparecer. Se a restauração falhar, preserve o JSON e confira driver, adaptador e a propriedade avançada `NetworkAddress` no Gerenciador de Dispositivos.

Nem MAC verificado nem link ativo provam conectividade útil. Confira IP, gateway, DNS e acesso permitido no escopo. Para alegações de MAB, 802.1X ou MACsec, correlacione sessão de autenticação e logs do switch/NAC; a captura no computador é evidência parcial.

`./PowerR.ps1 -Action Diagnose -Adapter '<GUID>'` mostra um retrato local de link, IPv4, gateway, DNS e contadores. Ele não envia tráfego de teste. Guarde saídas de cliente somente no diretório privado do engajamento.
