#include "ClassicLauncher.h"
#include "ShellLocalization.h"
#include <iostream>
#include <source_location>
namespace {
int checks{};
void Require(bool value,const std::source_location location=std::source_location::current()){if(!value)std::cerr<<"Assertion line "<<location.line()<<" error "<<GetLastError()<<"\n";fh::Check(value);++checks;}
template<class F> void Reject(F&& action){bool rejected=false;try{action();}catch(...){rejected=true;}Require(rejected);}
void File(const std::wstring& path,const std::string& bytes="test") {
 auto actual=path.size()>=MAX_PATH&&!path.starts_with(L"\\\\?\\")?L"\\\\?\\"+path:path;
 fh::Handle file(CreateFileW(actual.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));Require(file.valid());
 DWORD written{};Require(WriteFile(file.value,bytes.data(),static_cast<DWORD>(bytes.size()),&written,nullptr)&&written==bytes.size());
}
DWORD Run(const std::wstring& executable,const std::wstring& root){
 auto command=L"\""+executable+L"\"";STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};
 Require(CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,root.c_str(),&startup,&process)!=FALSE);
 fh::Handle child(process.hProcess),thread(process.hThread);Require(WaitForSingleObject(child.value,10000)==WAIT_OBJECT_0);DWORD code{};Require(GetExitCodeProcess(child.value,&code)!=FALSE);return code;
}
void AgentChecks(const std::wstring& directory,const std::wstring& root){
 auto launcher=root+L"\\FluffHistory.Agent.exe";Require(CopyFileW((directory+L"\\FluffHistory.AgentLauncher.exe").c_str(),launcher.c_str(),TRUE)!=FALSE);
 Require(Run(launcher,root)==1);auto marker=root+L"\\current-version.txt";File(marker,"malformed");Require(Run(launcher,root)==1);Require(DeleteFileW(marker.c_str())!=FALSE);
 auto versions=root+L"\\Versions",version=versions+L"\\1.1.0.7";Require(CreateDirectoryW(versions.c_str(),nullptr)!=FALSE);Require(CreateDirectoryW(version.c_str(),nullptr)!=FALSE);File(marker,"1.1.0.7");Require(Run(launcher,root)==1);
 wchar_t self[32768];Require(GetModuleFileNameW(nullptr,self,32768)>0);auto agent=version+L"\\FluffHistory.Agent.exe";Require(CopyFileW(self,agent.c_str(),TRUE)!=FALSE);Require(Run(launcher,root)==0);
 auto ready=root+L"\\agent.ready";auto deadline=GetTickCount64()+10000;while(GetFileAttributesW(ready.c_str())==INVALID_FILE_ATTRIBUTES&&GetTickCount64()<deadline)Sleep(10);Require(GetFileAttributesW(ready.c_str())!=INVALID_FILE_ATTRIBUTES);
 Require(DeleteFileW(ready.c_str())!=FALSE);while(!DeleteFileW(agent.c_str())&&GetTickCount64()<deadline)Sleep(10);Require(GetFileAttributesW(agent.c_str())==INVALID_FILE_ATTRIBUTES);
 Require(RemoveDirectoryW(version.c_str())!=FALSE);Require(RemoveDirectoryW(versions.c_str())!=FALSE);Require(DeleteFileW(marker.c_str())!=FALSE);Require(DeleteFileW(launcher.c_str())!=FALSE);
}
}
int wmain(int argc,wchar_t**argv){
 if(argc==1 && std::wstring(argv[0]).ends_with(L"FluffHistory.Agent.exe")){fh::Handle ready(CreateFileW(L"agent.ready",GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));return ready.valid()?0:1;}
 std::wstring root;
 try {
  Require(argc==2);wchar_t temporary[32768];Require(GetTempPathW(32768,temporary)>0);
  root=std::wstring(temporary)+L"Classic launcher "+fh::RandomToken();Require(CreateDirectoryW(root.c_str(),nullptr)!=FALSE);
  AgentChecks(argv[1],root);
  for(const auto* raw:{L"\"C:\\one file.txt\"",L"C:\\one.txt",L"  \"C:\\one.txt\"  "})Require(fh::ClassicArgumentSyntax(raw));
  for(const auto* raw:{L"",L"   ",L"\"\"",L"\"C:\\one.txt",L"C:\\one\".txt\"",L"\"C:\\one.txt\" \"C:\\two.txt\"",L"C:\\one.txt C:\\two.txt"})Require(!fh::ClassicArgumentSyntax(raw));
  auto file=root+L"\\Unicode \x65e5\x672c ' & (test).txt";File(file);
  const wchar_t* args[]={L"launcher",file.c_str(),L"extra"};
  Reject([&]{fh::ClassicSelection(0,nullptr);});Reject([&]{fh::ClassicSelection(1,args);});Reject([&]{fh::ClassicSelection(3,args);});
  auto selected=fh::ClassicSelection(2,args);Require(selected.path==file && selected.kind==0);
  for(const auto* bad:{L"",L"relative.txt",L"C:relative",L"\\rooted",L"shell:Desktop",L"https://example.test",L"\\\\.\\NUL",L"C:\\one\\..\\two",L"C:\\file:stream",L"C:\\file\" extra",L"C:\\trailing ",L"C:\\folder\\",L"\\\\?\\GLOBALROOT\\Device\\HarddiskVolume1\\file"}) {
   const wchar_t* invalid[]={L"launcher",bad};Reject([&]{fh::ClassicSelection(2,invalid);});
  }
  std::wstring huge(32761,L'a');const wchar_t* oversized[]={L"launcher",huge.c_str()};Reject([&]{fh::ClassicSelection(2,oversized);});
  const wchar_t* folder[]={L"launcher",root.c_str()};Require(fh::ClassicSelection(2,folder).kind==1);
  auto unicodeFolder=root+L"\\\x65e5\x672c \x0645\x0644\x0641";Require(CreateDirectoryW(unicodeFolder.c_str(),nullptr)!=FALSE);
  const wchar_t* unicodeInput[]={L"launcher",unicodeFolder.c_str()};Require(fh::ClassicSelection(2,unicodeInput).path==unicodeFolder);Require(RemoveDirectoryW(unicodeFolder.c_str())!=FALSE);
  std::vector<std::wstring> longDirectories;auto longDirectory=root;
  while(longDirectory.size()<285){longDirectory+=L"\\"+std::wstring(60,L'x');Require(CreateDirectoryW((L"\\\\?\\"+longDirectory).c_str(),nullptr)!=FALSE);longDirectories.push_back(longDirectory);}
  auto longFile=longDirectory+L"\\long.txt";File(longFile);const wchar_t* plainLong[]={L"launcher",longFile.c_str()};Require(fh::ClassicSelection(2,plainLong).path==longFile);
  Require(DeleteFileW((L"\\\\?\\"+longFile).c_str())!=FALSE);for(auto i=longDirectories.rbegin();i!=longDirectories.rend();++i)Require(RemoveDirectoryW((L"\\\\?\\"+*i).c_str())!=FALSE);
  auto extended=L"\\\\?\\"+file;const wchar_t* longInput[]={L"launcher",extended.c_str()};Require(fh::ClassicSelection(2,longInput).path==extended);
  auto spaced=L"\\\\?\\"+root+L"\\trailing ";File(spaced);const wchar_t* spacedInput[]={L"launcher",spaced.c_str()};Require(fh::ClassicSelection(2,spacedInput).path==spaced);Require(DeleteFileW(spaced.c_str())!=FALSE);
  // This is the exact registered command shape; no interpreter or user-data switches.
  auto command=L"\"C:\\Program Files\\Fluff-History\\FluffHistory.ShellLauncher.exe\" \""+file+L"\"";
  int parsedCount{};auto parsed=CommandLineToArgvW(command.c_str(),&parsedCount);Require(parsed && parsedCount==2);Require(std::wstring(parsed[1])==file);LocalFree(parsed);
  auto frame=fh::OpenFrame(selected);fh::Reader reader{frame};Require(reader.header()==1);Require(reader.u32()==0);Require(reader.text(32760)==file);reader.end();
  auto requests=root+L"\\requests";Require(CreateDirectoryW(requests.c_str(),nullptr)!=FALSE);
  std::wstring request;
  auto activation=[&](const std::wstring& arguments){Require(fh::ActivationArguments(arguments));auto token=arguments.substr(16);Require(fh::Token(token));request=requests+L"\\"+token+L".request";Require(GetFileAttributesW(request.c_str())!=INVALID_FILE_ATTRIBUTES);return E_FAIL;};
  Require(FAILED(fh::BrowseInto(selected,requests,activation)));Require(GetFileAttributesW(request.c_str())==INVALID_FILE_ATTRIBUTES);
  Require(FAILED(fh::BrowseInto(selected,requests,[&](const std::wstring& arguments)->HRESULT{activation(arguments);throw std::runtime_error("child creation");})));Require(GetFileAttributesW(request.c_str())==INVALID_FILE_ATTRIBUTES);
  Require(SUCCEEDED(fh::BrowseInto(selected,requests,[&](const std::wstring& arguments){activation(arguments);return S_OK;})));Require(DeleteFileW(request.c_str())!=FALSE);Require(RemoveDirectoryW(requests.c_str())!=FALSE);
  auto link=root+L"\\link";
  if(CreateSymbolicLinkW(link.c_str(),root.c_str(),SYMBOLIC_LINK_FLAG_DIRECTORY|SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE)){
   auto linked=link+file.substr(root.size());const wchar_t* input[]={L"launcher",linked.c_str()};Reject([&]{fh::ClassicSelection(2,input);});Require(RemoveDirectoryW(link.c_str())!=FALSE);
  } else std::cout<<"Reparse fixture unavailable without symlink privilege\n";
  auto executable=std::wstring(argv[1])+L"\\FluffHistory.ShellLauncher.exe";
  auto resources=LoadLibraryExW(executable.c_str(),nullptr,LOAD_LIBRARY_AS_DATAFILE|LOAD_LIBRARY_AS_IMAGE_RESOURCE);Require(resources!=nullptr);
  for(const auto* locale:{L"en-US",L"de-DE",L"fr-FR",L"ar-SA",L"ja-JP"}){
   auto lang=LANGIDFROMLCID(LocaleNameToLCID(locale,0));
   auto table=FindResourceExW(resources,RT_STRING,MAKEINTRESOURCEW(3000/16+1),lang);Require(table!=nullptr);
  }
  wchar_t title[4096];Require(LoadStringW(resources,3000,title,4096)>0);
  auto icon=static_cast<HICON>(LoadImageW(resources,MAKEINTRESOURCEW(1),IMAGE_ICON,20,20,LR_DEFAULTCOLOR));Require(icon!=nullptr);DestroyIcon(icon);FreeLibrary(resources);
  Require(DeleteFileW(file.c_str())!=FALSE);Require(RemoveDirectoryW(root.c_str())!=FALSE);
  std::cout<<"Classic Shell launcher checks passed: "<<checks<<"\n";return 0;
 } catch(...) { std::cerr<<"Classic launcher check failed after "<<checks<<" assertions; fixture retained for diagnosis\n";return 1; }
}
