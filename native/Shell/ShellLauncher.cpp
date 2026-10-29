#include "ClassicLauncher.h"
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR arguments,int) {
 if(!arguments || !fh::ClassicArgumentSyntax(arguments))return 1;
 int argc{};auto argv=CommandLineToArgvW(GetCommandLineW(),&argc);
 if(!argv)return 1;
 auto result=fh::ClassicLaunch(argc,argv);LocalFree(argv);return result;
}
