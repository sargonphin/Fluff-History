#include "ShellCommon.h"
#include "ShellLocalization.h"

class ExplorerCommand final: public IExplorerCommand {
 std::atomic<ULONG> refs{1};
public:
 ExplorerCommand(){++fh::objects;}~ExplorerCommand(){--fh::objects;}
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void**p) override{if(!p)return E_POINTER;*p=nullptr;if(iid!=IID_IUnknown&&iid!=IID_IExplorerCommand)return E_NOINTERFACE;*p=static_cast<IExplorerCommand*>(this);AddRef();return S_OK;}
 ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
 ULONG STDMETHODCALLTYPE Release() override{auto n=--refs;if(!n)delete this;return n;}
 HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*,PWSTR*p) override {return fh::Boundary([&](){if(!p)return E_POINTER;*p=nullptr;auto title=fh::Text(fh::TextId::Shell_CommandTitle);return SHStrDupW(title.c_str(),p);});}
 HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*,PWSTR*p) override {return fh::Boundary([&](){if(!p)return E_POINTER;*p=nullptr;wchar_t path[32768];auto count=GetModuleFileNameW(fh::module,path,32768);if(!count||count>=32768)return E_FAIL;auto resource=std::wstring(path)+L",-101";return SHStrDupW(resource.c_str(),p);});}
 HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*,PWSTR*p) override {if(!p)return E_POINTER;*p=nullptr;return E_NOTIMPL;}
 HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID*p) override {if(!p)return E_POINTER;*p=fh::CommandId;return S_OK;}
 HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items,BOOL,EXPCMDSTATE*p) override {if(!p)return E_POINTER;*p=ECS_HIDDEN;return fh::Boundary([&](){if(SUCCEEDED(fh::Selected(items)))*p=ECS_ENABLED;return S_OK;});}
 HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items,IBindCtx*) override {return fh::Boundary([&](){fh::Selection selected;auto hr=fh::Selected(items,&selected);return FAILED(hr)?hr:fh::Browse(selected);});}
 HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS*p) override {if(!p)return E_POINTER;*p=ECF_DEFAULT;return S_OK;}
 HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand**p) override {if(!p)return E_POINTER;*p=nullptr;return E_NOTIMPL;}
};
STDAPI DllGetClassObject(REFCLSID cls,REFIID iid,void**p){return fh::ClassObject<ExplorerCommand>(cls,fh::CommandId,iid,p);}
STDAPI DllCanUnloadNow(){return fh::objects==0?S_OK:S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,void*) {if(reason==DLL_PROCESS_ATTACH){fh::module=h;DisableThreadLibraryCalls(h);}return TRUE;}
