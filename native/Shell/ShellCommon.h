#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <commctrl.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <vector>
#include <stdexcept>

using Microsoft::WRL::ComPtr;
namespace fh {
inline HINSTANCE module{};
inline std::atomic<long> objects{};
inline constexpr uint32_t Magic = 0x31485346, MaxBody = 128 * 1024;
inline constexpr CLSID PropertiesId = {0x80f01e21,0xb634,0x4183,{0x83,0x4e,0xcf,0x09,0xd7,0x24,0x24,0x02}};
struct Handle {
 HANDLE value = INVALID_HANDLE_VALUE;
 Handle() = default; explicit Handle(HANDLE h):value(h){}
 ~Handle(){if(value && value!=INVALID_HANDLE_VALUE)CloseHandle(value);}
 Handle(const Handle&)=delete; Handle& operator=(const Handle&)=delete;
 bool valid() const {return value && value!=INVALID_HANDLE_VALUE;}
};
template<class F> HRESULT Boundary(F&& action) noexcept {
 try { return action(); } catch(const std::bad_alloc&) {return E_OUTOFMEMORY;} catch(...) {return E_FAIL;}
}
inline void Check(bool condition){if(!condition)throw std::runtime_error("Shell operation failed");}
struct Selection {std::wstring path; uint32_t kind{};};
inline HRESULT Selected(IShellItemArray* items, Selection* output = nullptr) {
 if(!items)return E_INVALIDARG;
 DWORD count{}; HRESULT hr=items->GetCount(&count);if(FAILED(hr)||count!=1)return E_INVALIDARG;
 ComPtr<IShellItem> item;hr=items->GetItemAt(0,&item);if(FAILED(hr))return hr;
 SFGAOF attributes{};hr=item->GetAttributes(SFGAO_FILESYSTEM|SFGAO_FOLDER|SFGAO_STREAM,&attributes);
 if(FAILED(hr)||!(attributes&SFGAO_FILESYSTEM))return E_INVALIDARG;
 if(output){
  PWSTR raw{};hr=item->GetDisplayName(SIGDN_FILESYSPATH,&raw);if(FAILED(hr))return hr;
  std::unique_ptr<wchar_t,decltype(&CoTaskMemFree)> path(raw,CoTaskMemFree);
  if(!raw || wcslen(raw)==0 || wcslen(raw)>32760)return E_INVALIDARG;
  output->path=raw;output->kind=(attributes&SFGAO_FOLDER)&&!(attributes&SFGAO_STREAM)?1u:0u;
 }
 return S_OK;
}
inline bool Token(const std::wstring& value){
 if(value.size()!=32)return false;
 for(auto c:value)if(!((c>=L'0'&&c<=L'9')||(c>=L'a'&&c<=L'f')))return false;
 return true;
}
inline std::wstring RandomToken(){
 UCHAR bytes[16];Check(BCryptGenRandom(nullptr,bytes,sizeof(bytes),BCRYPT_USE_SYSTEM_PREFERRED_RNG)==0);
 const wchar_t digits[]=L"0123456789abcdef";std::wstring token;token.reserve(32);
 for(auto b:bytes){token.push_back(digits[b>>4]);token.push_back(digits[b&15]);}return token;
}
struct Writer {
 std::vector<uint8_t> bytes;
 void u32(uint32_t n){auto p=reinterpret_cast<const uint8_t*>(&n);bytes.insert(bytes.end(),p,p+4);}
 void u64(uint64_t n){auto p=reinterpret_cast<const uint8_t*>(&n);bytes.insert(bytes.end(),p,p+8);}
 void text(const std::wstring& s,uint32_t max){Check(s.size()<=max && s.find(L'\0')==std::wstring::npos);u32(static_cast<uint32_t>(s.size()));auto p=reinterpret_cast<const uint8_t*>(s.data());bytes.insert(bytes.end(),p,p+s.size()*2);}
 std::vector<uint8_t> frame(uint32_t operation) const {
  Check(bytes.size()<=MaxBody);Writer w;w.u32(Magic);w.u32(1);w.u32(operation);w.u32(static_cast<uint32_t>(bytes.size()));w.bytes.insert(w.bytes.end(),bytes.begin(),bytes.end());return w.bytes;
 }
};
struct Reader {
 const std::vector<uint8_t>& bytes;size_t position{};
 uint32_t u32(){Check(bytes.size()-position>=4);uint32_t n;memcpy(&n,bytes.data()+position,4);position+=4;return n;}
 uint64_t u64(){Check(bytes.size()-position>=8);uint64_t n;memcpy(&n,bytes.data()+position,8);position+=8;return n;}
 std::wstring text(uint32_t max){auto n=u32();Check(n<=max && static_cast<size_t>(n)*2<=bytes.size()-position);std::wstring s(n,L'\0');memcpy(s.data(),bytes.data()+position,n*2);position+=n*2;Check(s.find(L'\0')==std::wstring::npos);if(n)Check(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,s.data(),static_cast<int>(n),nullptr,0,nullptr,nullptr)>0);return s;}
 void end(){Check(position==bytes.size());}
 uint32_t header(){Check(bytes.size()>=16 && bytes.size()<=16+MaxBody);Check(u32()==Magic && u32()==1);auto operation=u32();Check(u32()==bytes.size()-16);return operation;}
};
inline std::vector<uint8_t> OpenFrame(const Selection& selection){Writer w;w.u32(selection.kind);w.text(selection.path,32760);return w.frame(1);}
inline void OrdinaryDirectories(const std::wstring& path){
 auto current=path;
 while(current.size()>3){auto attributes=GetFileAttributesW(current.c_str());Check(attributes!=INVALID_FILE_ATTRIBUTES && (attributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))==FILE_ATTRIBUTE_DIRECTORY);auto i=current.find_last_of(L"\\/");if(i==std::wstring::npos)break;current.resize(i);}
}
inline std::wstring RequestsDirectory(){
 PWSTR raw{};Check(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData,0,nullptr,&raw)));
 std::unique_ptr<wchar_t,decltype(&CoTaskMemFree)> free(raw,CoTaskMemFree);std::wstring path(raw);
 OrdinaryDirectories(path);path+=L"\\Fluff-History";Check(CreateDirectoryW(path.c_str(),nullptr)||GetLastError()==ERROR_ALREADY_EXISTS);OrdinaryDirectories(path);
 path+=L"\\ShellRequests";Check(CreateDirectoryW(path.c_str(),nullptr)||GetLastError()==ERROR_ALREADY_EXISTS);OrdinaryDirectories(path);return path;
}
inline bool ActivationArguments(const std::wstring& arguments){
 for(const auto* prefix:{L"--shell-request ",L"--shell-broker "}){
  auto length=wcslen(prefix);if(arguments.compare(0,length,prefix)==0)return Token(arguments.substr(length));
 }
 return false;
}
inline std::wstring InstalledDesktop(const std::wstring& root){
 // Resolve the active Setup version, not the version of an already-loaded Shell DLL.
 // Both native entry points work without package identity.
 OrdinaryDirectories(root);
 Handle marker(CreateFileW((root+L"\\current-version.txt").c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr));
 Check(marker.valid());FILE_ATTRIBUTE_TAG_INFO attributes{};LARGE_INTEGER size{};char bytes[32]{};DWORD read{};
 Check(GetFileInformationByHandleEx(marker.value,FileAttributeTagInfo,&attributes,sizeof(attributes)) &&
  !(attributes.FileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY)) &&
  GetFileSizeEx(marker.value,&size) && size.QuadPart>0 && size.QuadPart<static_cast<LONGLONG>(sizeof(bytes)) &&
  ReadFile(marker.value,bytes,static_cast<DWORD>(size.QuadPart),&read,nullptr) && read==static_cast<DWORD>(size.QuadPart));
 std::string version(bytes,read);if(!version.empty() && version.back()=='\n'){version.pop_back();if(!version.empty() && version.back()=='\r')version.pop_back();}
 unsigned parts{},value{},digits{};bool nonzero=false;std::wstring wide;
 for(char c:version){
  if(c=='.'){Check(digits>0 && parts<3);++parts;digits=0;value=0;}
  else{Check(c>='0' && c<='9' && digits<5 && !(digits && value==0));value=value*10+static_cast<unsigned>(c-'0');Check(value<=65535);++digits;nonzero|=value!=0;}
  wide.push_back(static_cast<wchar_t>(c));
 }
 Check(parts==3 && digits>0 && nonzero);
 auto directory=root+L"\\Versions\\"+wide;OrdinaryDirectories(directory);
 auto executable=directory+L"\\FluffHistory.Desktop.exe";auto flags=GetFileAttributesW(executable.c_str());
 Check(flags!=INVALID_FILE_ATTRIBUTES && !(flags&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)));return executable;
}
inline HRESULT ActivateInstalled(const std::wstring& root,const std::wstring& arguments){
 return Boundary([&](){if(!ActivationArguments(arguments))return E_INVALIDARG;
  auto executable=InstalledDesktop(root);auto command=L"\""+executable+L"\" "+arguments;Check(command.size()<32767);
  STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};
  if(!CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,root.c_str(),&startup,&process))return HRESULT_FROM_WIN32(GetLastError());
  Handle thread(process.hThread),child(process.hProcess);return S_OK;
 });
}
inline HRESULT Activate(const std::wstring& arguments){
 return Boundary([&](){PWSTR raw{};auto hr=SHGetKnownFolderPath(FOLDERID_LocalAppData,0,nullptr,&raw);if(FAILED(hr))return hr;
  std::unique_ptr<wchar_t,decltype(&CoTaskMemFree)> free(raw,CoTaskMemFree);
  return ActivateInstalled(std::wstring(raw)+L"\\Programs\\Fluff-History",arguments);
 });
}
template<class ActivateRequest> HRESULT BrowseInto(const Selection& selection,const std::wstring& directory,ActivateRequest&& activate){
 return Boundary([&](){auto token=RandomToken();auto path=directory+L"\\"+token+L".request";
  auto bytes=OpenFrame(selection);Handle file(CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr));Check(file.valid());
  DWORD written{};bool ok=WriteFile(file.value,bytes.data(),static_cast<DWORD>(bytes.size()),&written,nullptr)!=FALSE && written==bytes.size();
  if(!ok){CloseHandle(file.value);file.value=INVALID_HANDLE_VALUE;DeleteFileW(path.c_str());return E_FAIL;}
  CloseHandle(file.value);file.value=INVALID_HANDLE_VALUE;
  HRESULT result=E_FAIL;
  try { result=activate(L"--shell-request "+token); } catch(...) { DeleteFileW(path.c_str());throw; }
  if(FAILED(result))DeleteFileW(path.c_str());return result;
 });
}
inline HRESULT Browse(const Selection& selection){return Boundary([&](){return BrowseInto(selection,RequestsDirectory(),Activate);});}
template<class Object> class Factory final:public IClassFactory {
 std::atomic<ULONG> refs{1};
public:
 Factory(){++objects;}~Factory(){--objects;}
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void**p) override {if(!p)return E_POINTER;*p=nullptr;if(iid!=IID_IUnknown&&iid!=IID_IClassFactory)return E_NOINTERFACE;*p=static_cast<IClassFactory*>(this);AddRef();return S_OK;}
 ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
 ULONG STDMETHODCALLTYPE Release() override{auto n=--refs;if(!n)delete this;return n;}
 HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void**p) override {return Boundary([&](){if(!p)return E_POINTER;*p=nullptr;if(outer)return CLASS_E_NOAGGREGATION;auto object=new Object;auto hr=object->QueryInterface(iid,p);object->Release();return hr;});}
 HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override{if(lock)++objects;else --objects;return S_OK;}
};
template<class Object> HRESULT ClassObject(REFCLSID cls,REFCLSID expected,REFIID iid,void**p) noexcept {
 return Boundary([&](){if(!p)return E_POINTER;*p=nullptr;if(cls!=expected)return CLASS_E_CLASSNOTAVAILABLE;auto factory=new Factory<Object>;auto hr=factory->QueryInterface(iid,p);factory->Release();return hr;});
}
}
