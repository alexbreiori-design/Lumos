# Lumos

Controle de brilho por monitor no Windows — bandeja do sistema, DDC/CI quando disponível e camada de escurecimento quando não estiver.

## Recursos

- Ajuste global ou por monitor
- Perfis com ícone, nome e níveis salvos
- Pausar monitores individualmente
- Tema com cor de destaque personalizável
- Início com o Windows (opcional)

## Requisitos

- Windows 10 ou 11 (x64)
- O instalador já inclui o runtime (.NET) — não é preciso instalar o SDK

## Instalação

Baixe o `Lumos-Setup-*.exe` na [página de Releases](https://github.com/alexbreiori-design/Lumos/releases) e execute.

## Desenvolvimento

```powershell
dotnet build
dotnet run --project Lumos.csproj
```

### Publicar e gerar instalador

```powershell
.\scripts\build-installer.ps1
```

O instalador sai em `dist\Lumos-Setup-1.1.0.exe` (requer [Inno Setup 6](https://jrsoftware.org/isinfo.php)).

Só publicar os binários:

```powershell
.\scripts\publish.ps1
```

## Licença

Uso pessoal e distribuição conforme o repositório.
