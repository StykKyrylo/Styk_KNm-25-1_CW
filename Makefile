.PHONY: setup test reproduce-small reproduce

STUDENT_NUMBER ?= 1

setup:
	dotnet restore KursovaKP08.sln
	dotnet build KursovaKP08.sln -c Release --no-restore

test: setup
	dotnet test tests/KursovaKP08.Tests.csproj -c Release --no-build

reproduce-small: setup test
	dotnet run --project src/KursovaKP08.csproj -c Release --no-build -- --profile small --student-number $(STUDENT_NUMBER)

reproduce: setup test
	dotnet run --project src/KursovaKP08.csproj -c Release --no-build -- --profile full --student-number $(STUDENT_NUMBER)
