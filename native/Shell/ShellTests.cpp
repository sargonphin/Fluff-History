#include "ShellProtocol.h"
#include "ShellLocalization.h"
#include <iostream>

namespace {
int checks{};
void Require(bool value){fh::Check(value);++checks;}
template<class F> void Reject(F&& action){bool rejected=false;try{action();}catch(...){rejected=true;}Require(rejected);}
using GetClass=HRESULT(STDAPICALLTYPE*)(REFCLSID,REFIID,void**);
using CanUnload=HRESULT(STDAPICALLTYPE*)();
BOOL CALLBACK AddPage(HPROPSHEETPAGE page,LPARAM data){*reinterpret_cast<HPROPSHEETPAGE*>(data)=page;return TRUE;}
struct TestLanguage {
 LANGID previous=GetThreadUILanguage();std::vector<wchar_t> names;
 explicit TestLanguage(const wchar_t* name){
  ULONG count{},size{};fh::Check(GetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,&count,nullptr,&size)!=FALSE);names.resize(size);
  fh::Check(GetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,&count,names.data(),&size)!=FALSE);
  auto id=LocaleNameToLCID(name,0);fh::Check(id!=0);SetThreadUILanguage(LANGIDFROMLCID(id));
  std::wstring selected(name);selected.push_back(L'\0');fh::Check(SetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,selected.c_str(),&count)!=FALSE);
 }
 ~TestLanguage(){SetThreadUILanguage(previous);ULONG ignored{};if(!names.empty())SetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,names.data(),&ignored);}
};
void ActivationChecks(){
 auto token=fh::RandomToken();
 for(const auto* prefix:{L"--shell-request ",L"--shell-broker "})Require(fh::ActivationArguments(std::wstring(prefix)+token));
 for(const auto& args:{L"",L"--shell-request ../bad",L"--shell-broker",L"--other aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",L"--shell-request aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa extra"})Require(!fh::ActivationArguments(args));
 wchar_t temporary[32768],self[32768];Require(GetTempPathW(32768,temporary)>0);Require(GetModuleFileNameW(nullptr,self,32768)>0);
 std::wstring root=std::wstring(temporary)+L"Shell activation "+token;Require(CreateDirectoryW(root.c_str(),nullptr)!=FALSE);
 auto versions=root+L"\\Versions";Require(CreateDirectoryW(versions.c_str(),nullptr)!=FALSE);
 auto first=versions+L"\\1.0.0.1",second=versions+L"\\1.0.0.2";
 for(const auto& directory:{first,second}){Require(CreateDirectoryW(directory.c_str(),nullptr)!=FALSE);Require(CopyFileW(self,(directory+L"\\FluffHistory.Desktop.exe").c_str(),TRUE)!=FALSE);}
 auto marker=root+L"\\current-version.txt";
 auto write=[&](const std::string& value){fh::Handle file(CreateFileW(marker.c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr));Require(file.valid());DWORD written{};Require(WriteFile(file.value,value.data(),static_cast<DWORD>(value.size()),&written,nullptr)&&written==value.size());};
 Require(FAILED(fh::ActivateInstalled(root,L"--shell-request "+token)));
 for(const auto& version:{"","0.0.0.0","1.0.0","1.0.0.65536","01.0.0.1","1.0.0.1\nignored","1.0.0.1/other","1.0.0.1\r","1.0.0.1.2"}){write(version);Reject([&]{fh::InstalledDesktop(root);});}
 write("1.0.0.1\r\n");Require(fh::InstalledDesktop(root)==first+L"\\FluffHistory.Desktop.exe");
 write("1.0.0.2\n");Require(fh::InstalledDesktop(root)==second+L"\\FluffHistory.Desktop.exe");
 Require(fh::ActivateInstalled(root,L"--shell-request ../bad")==E_INVALIDARG);
 Require(SUCCEEDED(fh::ActivateInstalled(root,L"--shell-broker "+token)));
 auto ready=root+L"\\"+token+L".ready";
 auto deadline=GetTickCount64()+10000;while(GetFileAttributesW(ready.c_str())==INVALID_FILE_ATTRIBUTES && GetTickCount64()<deadline)Sleep(10);
 Require(GetFileAttributesW(ready.c_str())!=INVALID_FILE_ATTRIBUTES);
 DeleteFileW(ready.c_str());DeleteFileW(marker.c_str());
 // The child has finished using the executable when Windows permits deletion.
 for(const auto& directory:{first,second}){auto executable=directory+L"\\FluffHistory.Desktop.exe";deadline=GetTickCount64()+10000;while(!DeleteFileW(executable.c_str())&&GetTickCount64()<deadline)Sleep(10);Require(GetFileAttributesW(executable.c_str())==INVALID_FILE_ATTRIBUTES);Require(RemoveDirectoryW(directory.c_str())!=FALSE);}
 Require(RemoveDirectoryW(versions.c_str())!=FALSE);Require(RemoveDirectoryW(root.c_str())!=FALSE);
}
}
int wmain(int argc,wchar_t**argv){
 if(argc==3 && std::wstring(argv[1])==L"--shell-broker" && fh::Token(argv[2])){
  fh::Handle ready(CreateFileW((std::wstring(argv[2])+L".ready").c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));return ready.valid()?0:1;
 }
 auto com=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
 try{
  Require(SUCCEEDED(com));Require(argc==2);std::wstring directory=argv[1];TestLanguage english(L"en-US");
  ActivationChecks();
  fh::Writer response;response.u32(0);response.text(std::wstring(32,L'a'),32);response.text(L"History",4096);response.text(L"D:\\Backup",32760);response.u32(1);response.u32(1);response.text(std::wstring(32,L'b'),32);response.text(L"Exact saved version",1024);
  auto good=response.frame(101);auto parsed=fh::ParseReply(good);Require(parsed.rows.size()==1&&parsed.restore);
  fh::Writer table;table.u32(0);table.text(std::wstring(32,L'a'),32);table.text(L"Select a version",4096);table.text(L"20 file versions",1024);table.u32(1);table.u32(20);
  for(unsigned i=0;i<20;++i){auto key=std::wstring(30,L'b');key.push_back(L"0123456789abcdef"[i/16]);key.push_back(L"0123456789abcdef"[i%16]);table.text(key,32);table.text(L"report.txt",1024);table.text(L"28/10/2026",1024);table.u64(100+i);table.u32(i==0?1u:i==19?2u:0u);}
  auto tableBytes=table.frame(103);auto detail=fh::ParseReply(tableBytes);Require(detail.rows.size()==20&&detail.rows[0].flags==1&&detail.rows[19].flags==2&&detail.location.empty());
  Require(detail.rows[19].modifiedTicks==119);auto malformed=tableBytes;malformed.back()=0xff;Reject([&]{fh::ParseReply(malformed);});
  malformed=tableBytes;malformed.push_back(0);Reject([&]{fh::ParseReply(malformed);});
  auto invalid=good;invalid[0]=0;Reject([&]{fh::ParseReply(invalid);});invalid=good;invalid.push_back(0);Reject([&]{fh::ParseReply(invalid);});
  invalid=good;invalid[16]=99;Reject([&]{fh::ParseReply(invalid);});invalid=good;invalid.resize(18);Reject([&]{fh::ParseReply(invalid);});
  fh::Writer huge;huge.bytes.resize(fh::MaxBody+1);Reject([&]{huge.frame(1);});Require(!fh::Token(L"../bad"));
  wchar_t temporary[MAX_PATH],file[MAX_PATH];Require(GetTempPathW(MAX_PATH,temporary)>0);Require(GetTempFileNameW(temporary,L"fhs",0,file)!=0);
  ComPtr<IShellItem> item;Require(SUCCEEDED(SHCreateItemFromParsingName(file,nullptr,IID_PPV_ARGS(&item))));
  ComPtr<IShellItemArray> items;Require(SUCCEEDED(SHCreateShellItemArrayFromShellItem(item.Get(),IID_PPV_ARGS(&items))));
  for(bool properties:{true}){
   auto path=directory+L"\\FluffHistory.PropertySheet.dll";auto dll=LoadLibraryW(path.c_str());Require(dll!=nullptr);
   auto get=reinterpret_cast<GetClass>(GetProcAddress(dll,"DllGetClassObject"));auto unload=reinterpret_cast<CanUnload>(GetProcAddress(dll,"DllCanUnloadNow"));Require(get&&unload&&unload()==S_OK);
   Require(LoadImageW(dll,MAKEINTRESOURCEW(1),IMAGE_ICON,32,32,LR_SHARED)!=nullptr);
   if(properties){
    for(auto sample:std::vector<std::pair<unsigned,bool>>{{0x0409,false},{0x0407,false},{0x040c,false},{0x0401,true},{0x0411,false}}){
     auto resource=FindResourceExW(dll,RT_DIALOG,MAKEINTRESOURCEW(201),static_cast<LANGID>(sample.first));Require(resource!=nullptr);
     auto bytes=static_cast<const uint8_t*>(LockResource(LoadResource(dll,resource)));Require(bytes!=nullptr&&SizeofResource(dll,resource)>=20);
     DWORD extended{};memcpy(&extended,bytes+8,4);Require(((extended&WS_EX_LAYOUTRTL)!=0)==sample.second);
    }
   }
   {ComPtr<IClassFactory> factory;Require(SUCCEEDED(get(fh::PropertiesId,IID_PPV_ARGS(&factory))));
    if(properties){
     ComPtr<IShellExtInit> init;Require(SUCCEEDED(factory->CreateInstance(nullptr,IID_PPV_ARGS(&init))));ComPtr<IDataObject> data;Require(SUCCEEDED(item->BindToHandler(nullptr,BHID_DataObject,IID_PPV_ARGS(&data))));Require(SUCCEEDED(init->Initialize(nullptr,data.Get(),nullptr)));
     ComPtr<IShellPropSheetExt> sheet;Require(SUCCEEDED(init.As(&sheet)));HPROPSHEETPAGE page{};Require(SUCCEEDED(sheet->AddPages(AddPage,reinterpret_cast<LPARAM>(&page)))&&page);Require(unload()==S_FALSE);Require(DestroyPropertySheetPage(page)!=FALSE);
    }
   }
   Require(unload()==S_OK);FreeLibrary(dll);
  }
  item.Reset();items.Reset();DeleteFileW(file);CoUninitialize();std::cout<<"Native Shell checks passed: "<<checks<<"\n";return 0;
 }catch(...){if(SUCCEEDED(com))CoUninitialize();std::cerr<<"Native Shell check failed after "<<checks<<" assertions\n";return 1;}
}
