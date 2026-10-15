# Testes e contribuição

Execute o teste sem privilégios administrativos:

```powershell
./tests/Smoke.ps1
```

Ele analisa a sintaxe, lista adaptadores, aceita um plano com MAC unicast e rejeita multicast, nulo e valores malformados. Não muda interfaces. A validação de aplicação, restauração e captura exige adaptador, TShark/Npcap e switch de laboratório; registre o resultado sem publicar MACs reais nem PCAPs de cliente.

Mudanças no fluxo de alteração devem preservar seleção por GUID, confirmação, backup antes da mutação, verificação e restauração por adaptador. Atualize os guias junto com os comandos.
