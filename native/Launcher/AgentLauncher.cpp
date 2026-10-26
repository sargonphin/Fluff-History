#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>

// Stable installer-owned Run target. Only a bounded version selects a fixed sibling agent executable.
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
 wchar_t path[32768]; DWORD length=GetModuleFileNameW(nullptr,path,32768);
 if(!length||length>=32768)return 1;
 std::wstring root(path,length);auto slash=root.find_last_of(L'\\');if(slash==std::wstring::npos)return 1;root.resize(slash);
 HANDLE marker=CreateFileW((root+L"\\current-version.txt").c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
 if(marker==INVALID_HANDLE_VALUE)return 1;
 FILE_ATTRIBUTE_TAG_INFO attributes{};char version[64]{};DWORD bytes{};
 bool okay=GetFileInformationByHandleEx(marker,FileAttributeTagInfo,&attributes,sizeof(attributes))&&
  !(attributes.FileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY))&&ReadFile(marker,version,63,&bytes,nullptr)&&bytes>0&&bytes<63;
 CloseHandle(marker);if(!okay)return 1;
 std::wstring value;int dots=0,component=0;
 for(DWORD i=0;i<bytes;i++){char c=version[i];if(c=='\r'||c=='\n')break;if(c=='.'){if(!component||component>5)return 1;dots++;component=0;}else if(c>='0'&&c<='9')component++;else return 1;value.push_back(c);}
 if(dots!=3||!component||component>5||value.size()>23)return 1;
 auto executable=root+L"\\Versions\\"+value+L"\\FluffHistory.Agent.exe";
 auto command=L"\""+executable+L"\"";STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};
 if(!CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,root.c_str(),&startup,&process))return 1;
 CloseHandle(process.hThread);CloseHandle(process.hProcess);return 0;
}
