# Arquitetura, dados e limites

`PowerR.ps1` usa `Get-NetAdapter` para inventário, `Set-NetAdapter` para aplicar o override e `Restart-NetAdapter` para ativá-lo. Para restaurar, lê o estado salvo e reverte a propriedade avançada `NetworkAddress` antes de reiniciar. O estado é isolado por GUID de adaptador. A verificação compara o MAC reportado pelo Windows até dez vezes, em intervalos de dois segundos.

`Capture` chama TShark no dispositivo Npcap `\Device\NPF_{GUID}`; limita a duração e mostra apenas resumo por MAC de origem. Não grava pacotes, nomes de cliente nem endereços IP. A saída na tela pode conter MACs de terceiros: trate-a como evidência do engajamento e não a publique neste repositório.

A captura pode perder quadros se o TAP entregar só uma direção, se o dispositivo estiver silencioso ou se o driver/Npcap não expuser a interface. O filtro contempla ARP, LLDP e DHCP; outros protocolos não entram no resumo. `Capture` não verifica identidade física, porta de switch ou autenticação de rede. Não há API remota nem serviço persistente.
