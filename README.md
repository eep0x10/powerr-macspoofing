<div align="center">

<img src="docs/assets/banner.png" alt="PowerR — ilustração de identidade da ferramenta" width="100%">

# PowerR

### Captura observável, troca controlada e restauração do MAC no Windows.

[![PowerShell 5.1+](https://img.shields.io/badge/PowerShell-5.1%2B-3465A4)](PowerR.ps1) [![Windows](https://img.shields.io/badge/plataforma-Windows-0078D4)](docs/OPERATIONS.md) [![Teste local](https://img.shields.io/badge/teste-local-7B61A8)](tests/Smoke.ps1)

[Início rápido](#início-rápido) · [Fluxo](#fluxo-de-trabalho) · [Documentação](#documentação) · [Limites](#limites)

</div>

PowerR ajuda a identificar endereços de origem vistos em uma porta monitorada, revisar um plano de alteração do adaptador Ethernet, aplicar um MAC explícito e restaurar a configuração anterior. O operador escolhe o adaptador e o endereço; a captura nunca dispara alteração automática.

> O banner é arte conceitual, não uma captura da ferramenta. Use apenas em testes internos autorizados e com acesso local de recuperação.

## Início rápido

Requisitos: Windows PowerShell 5.1 ou PowerShell 7, módulo NetAdapter do Windows. Administrador somente para aplicar ou restaurar. Para captura: Wireshark com TShark e Npcap instalados.

```powershell
git clone https://github.com/eep0x10/powerr-macspoofing.git
cd powerr-macspoofing
./PowerR.ps1 -Action Adapters
./PowerR.ps1 -Action Capture -Adapter 'Ethernet' -Seconds 20
./PowerR.ps1 -Action Plan -Adapter 'Ethernet' -Mac '02:11:22:33:44:55'
```

Após conferir adaptador, origem do MAC e acesso local, abra PowerShell como administrador:

```powershell
./PowerR.ps1 -Action Apply -Adapter 'Ethernet' -Mac '02:11:22:33:44:55' -AcknowledgeDisconnect
./PowerR.ps1 -Action Status -Adapter 'Ethernet'
./PowerR.ps1 -Action Diagnose -Adapter 'Ethernet'
./PowerR.ps1 -Action Restore -Adapter 'Ethernet' -AcknowledgeDisconnect
```

`Apply` e `Restore` pedem confirmação do PowerShell. Use `-WhatIf` para revisar uma alteração sem executá-la. O nome `Ethernet` é apenas exemplo: escolha o adaptador retornado por `Adapters`. O nome ou GUID identifica o mesmo alvo; o GUID é mais estável se o nome mudar.

## Fluxo de trabalho

| Etapa | O que a ferramenta faz | O que você confere |
| :--- | :--- | :--- |
| `Adapters` | Lista interfaces, GUID, estado, MAC e descrição sem TShark. | A interface física correta. |
| `Capture` | Escuta por 5 a 120 segundos e resume MACs de origem de ARP, LLDP e DHCP. Não salva PCAP. | Topologia, direção do TAP e se a origem pertence ao dispositivo pretendido. |
| `Plan` | Valida o MAC e mostra configuração atual, destino e caminho do backup. | O alvo e a interrupção esperada da conexão. |
| `Apply` | Salva estado local, altera só o adaptador selecionado, reinicia e verifica o MAC informado. | Estado de rede e registros do switch/NAC. |
| `Restore` | Recupera o override anterior, reinicia, verifica e só então remove o backup. | Retorno da conectividade e configuração de acesso. |
| `Diagnose` | Mostra link, IPv4, gateway, DNS e contadores de pacotes locais. | Se há configuração de rede útil; não infere autorização NAC. |

MAC multicast e nulo são rejeitados. Um backup existente impede aplicar novamente até restaurar a sessão anterior. Falha de verificação preserva o backup para recuperação manual.

## Documentação

- [Operação e recuperação](docs/OPERATIONS.md)
- [Arquitetura, dados e limites](docs/ARCHITECTURE.md)
- [Testes e contribuição](docs/TESTING.md)

O script gráfico original foi retirado porque seu comando de restauração tentava modificar todos os adaptadores e não guardava estado para recuperação. A versão anterior continua disponível no histórico Git.

## Limites

Um MAC visto em uma captura não comprova que a origem está conectada à porta pretendida. A ferramenta também não demonstra autenticação 802.1X, MAB, MACsec, autorização NAC ou acesso a uma VLAN. Confirme essas condições com a telemetria do switch e do servidor de autenticação. A alteração do MAC pode derrubar a sessão remota e ser recusada pelo driver. A validação local cobre sintaxe, planejamento e comportamento sem mutação; a alteração física requer laboratório autorizado.
