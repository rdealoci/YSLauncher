#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
PROJECT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
PROJECT="$PROJECT_DIR/YSLauncher.csproj"
OUTPUT_ROOT="$PROJECT_DIR/dist"
CONFIGURATION="${CONFIGURATION:-Release}"
HOST_OS="$(uname -s)"
BUILD_WIN=true
BUILD_LINUX=true
BUILD_MAC=true

if [ "$HOST_OS" = "Darwin" ]; then
    MAC_BUILD_SCRIPT="$SCRIPT_DIR/mac/build_dmg.sh"
    MAC_OUTPUT="$PROJECT_DIR/bin/YSLauncher.dmg"
else
    MAC_BUILD_SCRIPT="$SCRIPT_DIR/linux/build_app_linux.sh"
    MAC_OUTPUT="$PROJECT_DIR/bin/YSLauncher.dmg"
fi

for arg in "$@"; do
    case "$arg" in
        --win|--windows)
            BUILD_WIN=true; BUILD_LINUX=false; BUILD_MAC=false
            ;;
        --linux)
            BUILD_WIN=false; BUILD_LINUX=true; BUILD_MAC=false
            ;;
        --mac|--macos)
            BUILD_WIN=false; BUILD_LINUX=false; BUILD_MAC=true
            ;;
        --all)
            BUILD_WIN=true; BUILD_LINUX=true; BUILD_MAC=true
            ;;
        --help|-h)
            echo "Uso: $0 [opcoes]"
            echo ""
            echo "Opcoes:"
            echo "  (padrao)    Compila todas as plataformas suportadas (Windows, Linux e macOS Universal 2)"
            echo "  --win       Compila apenas para Windows x64"
            echo "  --linux     Compila apenas para Linux x64"
            echo "  --mac       Compila apenas para macOS Universal 2"
            echo "  --all       Compila todas as plataformas (Windows, Linux e macOS)"
            echo "  --help, -h  Exibe esta mensagem de ajuda"
            exit 0
            ;;
        *)
            echo "Opcao desconhecida: $arg"
            echo "Use '$0 --help' para ver as opcoes disponiveis."
            exit 1
            ;;
    esac
done

if command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
elif [ -x "${HOME}/.dotnet/dotnet" ]; then
    DOTNET="${HOME}/.dotnet/dotnet"
else
    echo "Erro: .NET SDK nao encontrado."
    exit 1
fi

if [ ! -f "$PROJECT" ]; then
    echo "Erro: projeto nao encontrado em $PROJECT"
    exit 1
fi

if [ "$BUILD_MAC" = true ] && [ ! -f "$MAC_BUILD_SCRIPT" ]; then
    echo "Erro: script de build Universal 2 nao encontrado: $MAC_BUILD_SCRIPT"
    exit 1
fi

TARGET_RIDS=()
TARGET_NAMES=()

if [ "$BUILD_WIN" = true ]; then
    TARGET_RIDS+=(win-x64)
    TARGET_NAMES+=(windows-x64)
fi

if [ "$BUILD_LINUX" = true ]; then
    TARGET_RIDS+=(linux-x64)
    TARGET_NAMES+=(linux-x64)
fi

TOTAL_STEPS=${#TARGET_RIDS[@]}
if [ "$BUILD_MAC" = true ]; then
    TOTAL_STEPS=$((TOTAL_STEPS + 1))
fi

mkdir -p "$OUTPUT_ROOT"

echo "YSLauncher: Compilacao Multiplataforma"
echo "Host detectado: $HOST_OS"
if [ "$BUILD_MAC" = false ]; then
    echo "Nota: macOS Universal 2 ignorado"
fi

CURRENT_STEP=1

for index in "${!TARGET_RIDS[@]}"; do
    rid="${TARGET_RIDS[$index]}"
    name="${TARGET_NAMES[$index]}"
    output="$OUTPUT_ROOT/$name"

    case "$output" in
        "$OUTPUT_ROOT"/*) rm -rf -- "$output" ;;
        *) echo "Erro: caminho de saida invalido: $output"; exit 1 ;;
    esac

    echo "[$CURRENT_STEP/$TOTAL_STEPS] Gerando $name ($rid - Single-File)..."
    "$DOTNET" publish "$PROJECT" \
        --configuration "$CONFIGURATION" \
        --runtime "$rid" \
        --self-contained true \
        --output "$output" \
        --nologo \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:PublishReadyToRun=false \
        -p:PublishTrimmed=false \
        -p:DebugType=None \
        -p:DebugSymbols=false

    find "$output" -type f -name '*.pdb' -delete

    if [ "$rid" = "win-x64" ] && [ -f "$output/YSLauncher.exe" ]; then
        cp -f "$output/YSLauncher.exe" "$OUTPUT_ROOT/YSLauncher.exe"
        mkdir -p "$PROJECT_DIR/bin"
        cp -f "$output/YSLauncher.exe" "$PROJECT_DIR/bin/YSLauncher.exe"
        if [ -d "$PROJECT_DIR/site" ]; then
            cp -f "$output/YSLauncher.exe" "$PROJECT_DIR/site/YSLauncher.exe"
        fi
    elif [ "$rid" = "linux-x64" ] && [ -f "$output/YSLauncher" ]; then
        cp -f "$output/YSLauncher" "$OUTPUT_ROOT/YSLauncher-linux"
        mkdir -p "$PROJECT_DIR/bin"
        cp -f "$output/YSLauncher" "$PROJECT_DIR/bin/YSLauncher-linux"
        cp -f "$PROJECT_DIR/Assets/logo.png" "$output/logo.png" 2>/dev/null || true
        tar -czf "$OUTPUT_ROOT/YSLauncher-Linux.tar.gz" -C "$output" YSLauncher logo.png
        cp -f "$OUTPUT_ROOT/YSLauncher-Linux.tar.gz" "$PROJECT_DIR/bin/YSLauncher-Linux.tar.gz"
    fi

    CURRENT_STEP=$((CURRENT_STEP + 1))
done

if [ "$BUILD_MAC" = true ]; then
    echo "[$CURRENT_STEP/$TOTAL_STEPS] Gerando macOS Universal 2 (Intel + Apple Silicon)..."
    bash "$MAC_BUILD_SCRIPT"

    if [ -f "$MAC_OUTPUT" ]; then
        cp -f "$MAC_OUTPUT" "$OUTPUT_ROOT/YSLauncher.dmg"
    fi
    if [ -f "$PROJECT_DIR/bin/YSLauncher-macOS-Universal.tar.gz" ]; then
        cp -f "$PROJECT_DIR/bin/YSLauncher-macOS-Universal.tar.gz" "$OUTPUT_ROOT/YSLauncher-macOS-Universal.tar.gz"
    fi
    if [ -d "$PROJECT_DIR/bin/YSLauncher.app" ]; then
        rm -rf "$OUTPUT_ROOT/YSLauncher.app"
        cp -R "$PROJECT_DIR/bin/YSLauncher.app" "$OUTPUT_ROOT/"
    fi

    if [ ! -f "$OUTPUT_ROOT/YSLauncher.dmg" ] && [ ! -f "$OUTPUT_ROOT/YSLauncher-macOS-Universal.tar.gz" ]; then
        echo "Erro: o pacote Universal 2 nao foi gerado em $OUTPUT_ROOT"
        exit 1
    fi
fi

echo ""
echo "Build concluida com sucesso."
echo "Binarios gerados em: $OUTPUT_ROOT"
if [ -f "$OUTPUT_ROOT/YSLauncher.exe" ]; then
    echo "   Windows (.exe): $OUTPUT_ROOT/YSLauncher.exe"
fi
if [ -f "$OUTPUT_ROOT/YSLauncher-linux" ]; then
    echo "   Linux:          $OUTPUT_ROOT/YSLauncher-linux"
fi
if [ "$BUILD_MAC" = true ]; then
    if [ -f "$OUTPUT_ROOT/YSLauncher.dmg" ]; then
        echo "   macOS (.dmg):   $OUTPUT_ROOT/YSLauncher.dmg"
    fi

    if [ -f "$OUTPUT_ROOT/YSLauncher-macOS-Universal.tar.gz" ]; then
        echo "   macOS (.tar.gz): $OUTPUT_ROOT/YSLauncher-macOS-Universal.tar.gz"
    fi
fi
