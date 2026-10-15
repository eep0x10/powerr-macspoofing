# Testes e contribuição

Execute o teste sem privilégios administrativos:

```powershell
./tests/Smoke.ps1
```

Ele analisa a sintaxe, lista adaptadores, aceita um plano com MAC unicast e rejeita multicast, nulo e valores malformados. Não muda interfaces. A validação de aplicação, restauração e captura exige adaptador, TShark/Npcap e switch de laboratório; registre o resultado sem publicar MACs reais nem PCAPs de cliente.

Para validar a compilação e o núcleo embutido:

```powershell
./scripts/build.ps1
dotnet ./desktop/bin/Release/net10.0-windows/win-x64/PowerR.dll --smoke
```

`--smoke` testa a comunicação interna sem abrir a janela nem alterar adaptadores. Para empacotar uma release, inclua `PowerR.exe`, README e guias em `docs/`, publique SHA-256 e confira downloads e renderização do README no GitHub. O `.exe` é executável Windows x64; compilação e smoke test não comprovam a troca física do MAC nem autenticação de rede.

Mudanças no fluxo de alteração devem preservar seleção por GUID, confirmação, backup antes da mutação, verificação e restauração por adaptador. Atualize os guias junto com os comandos.
