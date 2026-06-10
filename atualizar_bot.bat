@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "REPO_DIR=C:\Users\Caixa\Desktop\personal\g5_git"
set "REMOTE_URL=https://github.com/pmartins87/G5_git.git"
set "BRANCH=main"

echo ==========================================
echo        SINCRONIZADOR G5_git
echo ==========================================
echo.

cd /d "%REPO_DIR%"
if errorlevel 1 (
    echo ERRO: Nao foi possivel acessar a pasta:
    echo %REPO_DIR%
    pause
    exit /b 1
)

git --version >nul 2>nul
if errorlevel 1 (
    echo ERRO: Git nao encontrado no Windows.
    echo Instale o Git for Windows antes de continuar.
    pause
    exit /b 1
)

if not exist ".git" (
    echo Repositorio Git nao encontrado. Inicializando...
    git init
    if errorlevel 1 (
        echo ERRO ao inicializar o repositorio.
        pause
        exit /b 1
    )
)

echo.
echo ==========================================
echo        CONFIGURANDO REPOSITORIO REMOTO
echo ==========================================

git remote get-url origin >nul 2>nul
if errorlevel 1 (
    echo Adicionando origin:
    echo %REMOTE_URL%
    git remote add origin "%REMOTE_URL%"
) else (
    for /f "delims=" %%u in ('git remote get-url origin') do set "CURRENT_REMOTE=%%u"

    echo Origin atual:
    echo !CURRENT_REMOTE!
    echo.

    if /I not "!CURRENT_REMOTE!"=="%REMOTE_URL%" (
        echo Trocando origin para:
        echo %REMOTE_URL%
        git remote set-url origin "%REMOTE_URL%"
        if errorlevel 1 (
            echo ERRO ao alterar o repositorio remoto.
            pause
            exit /b 1
        )
    )
)

echo.
echo ==========================================
echo        CONFIGURANDO BRANCH LOCAL
echo ==========================================

for /f "delims=" %%b in ('git branch --show-current') do set "CURRENT_BRANCH=%%b"

if "%CURRENT_BRANCH%"=="" (
    echo Nenhum branch atual detectado. Criando branch %BRANCH%...
    git checkout -b %BRANCH%
) else (
    echo Branch atual: %CURRENT_BRANCH%

    if /I not "%CURRENT_BRANCH%"=="%BRANCH%" (
        echo Renomeando branch atual para %BRANCH%...
        git branch -M %BRANCH%
        if errorlevel 1 (
            echo ERRO ao renomear branch.
            pause
            exit /b 1
        )
    )
)

echo.
echo ==========================================
echo        VERIFICANDO CONFLITOS PENDENTES
echo ==========================================

git diff --name-only --diff-filter=U | findstr . >nul
if not errorlevel 1 (
    echo ERRO: Existem conflitos pendentes no repositorio.
    echo Resolva os conflitos antes de sincronizar.
    echo.
    git status
    pause
    exit /b 1
)

echo.
echo ==========================================
echo        STATUS ANTES DO COMMIT
echo ==========================================
git status
echo.

echo ==========================================
echo        ADICIONANDO ALTERACOES LOCAIS
echo ==========================================
git add -A
if errorlevel 1 (
    echo ERRO ao adicionar arquivos.
    pause
    exit /b 1
)

git diff --cached --quiet
if errorlevel 1 (
    echo.
    set /p commit_msg="Descreva a alteracao feita: "

    if "%commit_msg%"=="" (
        set "commit_msg=Atualizacao do projeto G5"
    )

    echo.
    echo Criando commit local...
    git commit -m "%commit_msg%"
    if errorlevel 1 (
        echo ERRO ao criar commit.
        echo Verifique se seu nome/e-mail do Git estao configurados.
        pause
        exit /b 1
    )
) else (
    echo Nenhuma alteracao local para commitar.
)

echo.
echo ==========================================
echo        BAIXANDO ALTERACOES DO GITHUB
echo ==========================================

git ls-remote --exit-code --heads origin %BRANCH% >nul 2>nul
if errorlevel 1 (
    echo O branch remoto ainda nao existe ou o repositorio esta vazio.
    echo Pulando download inicial.
) else (
    echo Baixando e mesclando alteracoes remotas...
    git pull --no-rebase origin %BRANCH%

    if errorlevel 1 (
        echo.
        echo ERRO: houve conflito ou falha ao baixar alteracoes.
        echo.
        echo O Git tentou juntar o que esta no PC com o que esta no GitHub,
        echo mas encontrou conflito que precisa de decisao manual.
        echo.
        echo Arquivos em conflito:
        git diff --name-only --diff-filter=U
        echo.
        echo Abra esses arquivos no Visual Studio, resolva os conflitos,
        echo depois rode:
        echo.
        echo git add -A
        echo git commit -m "Resolve conflitos"
        echo git push -u origin %BRANCH%
        echo.
        pause
        exit /b 1
    )
)

echo.
echo ==========================================
echo        ENVIANDO PARA O GITHUB
echo ==========================================

git push -u origin %BRANCH%
if errorlevel 1 (
    echo.
    echo ERRO: o push falhou.
    echo Possiveis causas:
    echo - Falha de login no GitHub
    echo - Token/senha expirado
    echo - Branch protegido
    echo - Alteracoes remotas novas apareceram durante o processo
    echo.
    pause
    exit /b 1
)

echo.
echo ==========================================
echo        SINCRONIZACAO CONCLUIDA
echo ==========================================
echo Repositorio sincronizado com:
echo %REMOTE_URL%
echo.
pause