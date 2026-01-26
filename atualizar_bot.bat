@echo off
:: Define o caminho da pasta
cd /d "C:\Users\Caixa\Desktop\personal\g5_git"

:: Mostra o status atual
echo ==========================================
echo           STATUS DO REPOSITORIO
echo ==========================================
git status
echo.

:: Adiciona todas as modificacoes (arquivos novos, editados e deletados)
git add .

:: Pergunta a descricao do commit
set /p commit_msg="Descreva a alteracao feita: "

:: Faz o commit com a mensagem digitada
git commit -m "%commit_msg%"

:: Envia para o seu repositorio remoto
echo.
echo Enviando para o GitHub...
git push

echo.
echo ==========================================
echo             SUCESSO!
echo ==========================================
pause