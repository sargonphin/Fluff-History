#include "ShellProtocol.h"
#include "ShellLocalization.h"
#include <algorithm>
#include <numeric>

namespace {
using fh::TextId;
constexpr UINT Updated=WM_APP+241;
struct State {
 fh::Selection selected;std::mutex mutex;fh::Handle cancel{CreateEventW(nullptr,TRUE,FALSE,nullptr)},command{CreateEventW(nullptr,FALSE,FALSE,nullptr)};
 std::atomic<bool> closed{};HWND window{};UINT_PTR generation{};int next=1;std::wstring version;
 HRESULT (*activate)(const std::wstring&) = fh::Activate; // Internal native-test seam; never supplied over IPC.
 PTP_CALLBACK_ENVIRON workerEnvironment{}; // Internal test pool; production uses the default environment, without a cleanup group.
 fh::Reply reply;bool busy=true;TextId status=TextId::Shell_Loading;std::wstring result,language=fh::UiLanguage();
 bool rendering{},oldestFirst{};
 explicit State(fh::Selection selection):selected(std::move(selection)){fh::Check(cancel.valid()&&command.valid());auto token=fh::RandomToken();generation=static_cast<UINT_PTR>(wcstoull(token.substr(0,15).c_str(),nullptr,16));}
 void publish(){std::lock_guard lock(mutex);if(!closed && window)PostMessageW(window,Updated,generation,0);}
};
void Transfer(HANDLE pipe,HANDLE cancel,uint8_t* bytes,DWORD size,bool write,ULONGLONG deadline){
 while(size){
  fh::Handle event{CreateEventW(nullptr,TRUE,FALSE,nullptr)};fh::Check(event.valid());OVERLAPPED operation{};operation.hEvent=event.value;DWORD done{};
  BOOL ok=write?WriteFile(pipe,bytes,size,&done,&operation):ReadFile(pipe,bytes,size,&done,&operation);
  if(!ok){fh::Check(GetLastError()==ERROR_IO_PENDING);auto now=GetTickCount64();DWORD remaining=now>=deadline?0:static_cast<DWORD>(deadline-now);HANDLE waits[]={cancel,event.value};auto wait=WaitForMultipleObjects(2,waits,FALSE,remaining);
   if(wait!=WAIT_OBJECT_0+1){CancelIoEx(pipe,&operation);GetOverlappedResult(pipe,&operation,&done,TRUE);throw std::runtime_error("Shell pipe cancelled");}
   fh::Check(GetOverlappedResult(pipe,&operation,&done,FALSE)!=FALSE);
  }
  fh::Check(done>0&&done<=size);bytes+=done;size-=done;
 }
}
void WriteFrame(HANDLE pipe,HANDLE cancel,std::vector<uint8_t> bytes){Transfer(pipe,cancel,bytes.data(),static_cast<DWORD>(bytes.size()),true,GetTickCount64()+5000);}
std::vector<uint8_t> ReadFrame(HANDLE pipe,HANDLE cancel,DWORD timeout){
 auto deadline=GetTickCount64()+timeout;std::vector<uint8_t> bytes(16);Transfer(pipe,cancel,bytes.data(),16,false,deadline);
 fh::Reader header{bytes};fh::Check(header.u32()==fh::Magic && header.u32()==1);header.u32();auto count=header.u32();fh::Check(count<=fh::MaxBody);
 bytes.resize(16+count);if(count)Transfer(pipe,cancel,bytes.data()+16,count,false,deadline);return bytes;
}
HANDLE Connect(const std::wstring& token,HANDLE cancel){
 auto name=L"\\\\.\\pipe\\FluffHistory.Shell.v1."+token;auto deadline=GetTickCount64()+15000;
 while(GetTickCount64()<deadline && WaitForSingleObject(cancel,0)!=WAIT_OBJECT_0){
  auto pipe=CreateFileW(name.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
  if(pipe!=INVALID_HANDLE_VALUE)return pipe;
  auto error=GetLastError();fh::Check(error==ERROR_FILE_NOT_FOUND||error==ERROR_PIPE_BUSY);WaitForSingleObject(cancel,100);
 }
 throw std::runtime_error("Shell broker unavailable");
}
struct Work {std::shared_ptr<State> state;HMODULE pin{};};
void CALLBACK Worker(PTP_CALLBACK_INSTANCE callback,void* context,PTP_WORK){
 std::unique_ptr<Work> work(static_cast<Work*>(context));FreeLibraryWhenCallbackReturns(callback,work->pin);
 auto state=std::move(work->state);auto com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);fh::Handle pipe;
 while(!state->closed){
  try {
   int command;std::wstring version,session;
   {std::lock_guard lock(state->mutex);command=state->next;version=state->version;session=state->reply.session;state->next=0;}
   if(command==1){
    if(pipe.valid()){CloseHandle(pipe.value);pipe.value=INVALID_HANDLE_VALUE;}
    auto token=fh::RandomToken();fh::Check(SUCCEEDED(state->activate(L"--shell-broker "+token)));pipe.value=Connect(token,state->cancel.value);
    WriteFrame(pipe.value,state->cancel.value,fh::TableOpenFrame(state->selected,state->language));auto reply=fh::ParseReply(ReadFrame(pipe.value,state->cancel.value,15000));
    {std::lock_guard lock(state->mutex);state->reply=std::move(reply);state->busy=false;state->result.clear();
     constexpr TextId statuses[]={TextId::Shell_BackedUp,TextId::Shell_Inactive,TextId::Shell_NotProtected,TextId::Shell_RecoveryRequired,TextId::Shell_Unavailable,TextId::Shell_Expired};state->status=statuses[state->reply.status];}
    state->publish();
   }else if(command==2||command==3){
    fh::Check(pipe.valid()&&fh::Token(session)&&fh::Token(version));HWND window;
    {std::lock_guard lock(state->mutex);window=state->window;}
    fh::Writer w;w.text(session,32);w.text(version,32);w.u64(reinterpret_cast<uint64_t>(window));WriteFrame(pipe.value,state->cancel.value,w.frame(command==2?2u:6u));
    auto bytes=ReadFrame(pipe.value,state->cancel.value,180000);fh::Reader reader{bytes};fh::Check(reader.header()==102);auto result=reader.text(4096);reader.end();
    {std::lock_guard lock(state->mutex);state->result=std::move(result);state->busy=false;}state->publish();
   }
  }catch(...){
   if(pipe.valid()){CloseHandle(pipe.value);pipe.value=INVALID_HANDLE_VALUE;}
   if(!state->closed){std::lock_guard lock(state->mutex);state->busy=false;state->reply.restore=false;state->status=TextId::Shell_UnavailableContinue;state->result=fh::Text(TextId::Shell_UnavailableContinue);}
   state->publish();
  }
   HANDLE waits[]={state->cancel.value,state->command.value};auto wait=WaitForMultipleObjects(2,waits,FALSE,1000);
   if(wait==WAIT_OBJECT_0)break;
   if(wait==WAIT_TIMEOUT){
    // Detect broker exit/idle expiry without blocking the page or reading unsolicited data.
    if(pipe.valid()&&!PeekNamedPipe(pipe.value,nullptr,0,nullptr,nullptr,nullptr)){
     CloseHandle(pipe.value);pipe.value=INVALID_HANDLE_VALUE;
     {std::lock_guard lock(state->mutex);state->reply.restore=false;}state->publish();
    }
   }else if(wait!=WAIT_OBJECT_0+1)break;
 }
 if(pipe.valid()){try{fh::Writer w;WriteFrame(pipe.value,state->cancel.value,w.frame(3));}catch(...) {}}
 if(SUCCEEDED(com))CoUninitialize();--fh::objects;
}
void StartWorker(const std::shared_ptr<State>& state){
 auto work=std::make_unique<Work>();work->state=state;
 fh::Check(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(&Worker),&work->pin)!=FALSE);
 auto callback=CreateThreadpoolWork(Worker,work.get(),state->workerEnvironment);if(!callback){FreeLibrary(work->pin);throw std::bad_alloc();}
 // CloseThreadpoolWork releases asynchronously after outstanding (including queued) callbacks;
 // it does not cancel them. Worker retains the context/count/pin until it runs, even after page close.
 ++fh::objects;work.release();SubmitThreadpoolWork(callback);CloseThreadpoolWork(callback);
}
struct Page {std::shared_ptr<State> state;};
int SelectedRow(HWND window){
 auto list=GetDlgItem(window,2103);auto selected=ListView_GetNextItem(list,-1,LVNI_SELECTED);if(selected<0)return -1;
 LVITEMW item{};item.mask=LVIF_PARAM;item.iItem=selected;
 return ListView_GetItem(list,&item)?static_cast<int>(item.lParam):-1;
}
void Actions(HWND window,const std::shared_ptr<State>& state){
 auto row=SelectedRow(window);auto enabled=!state->busy&&state->reply.restore&&row>=0&&static_cast<size_t>(row)<state->reply.rows.size();
 EnableWindow(GetDlgItem(window,2104),enabled);EnableWindow(GetDlgItem(window,2106),enabled);
}
void Render(HWND window,const std::shared_ptr<State>& state){
 std::lock_guard lock(state->mutex);state->rendering=true;
 struct Reset {bool& flag;~Reset(){flag=false;}} reset{state->rendering};
 SetDlgItemTextW(window,2101,fh::Text(state->selected.kind?TextId::Shell_IntroFolder:TextId::Shell_IntroFile).c_str());
 auto text=state->busy?fh::Text(state->status):state->result.empty()?state->reply.summary:state->result;
 SetDlgItemTextW(window,2102,text.c_str());
 SetDlgItemTextW(window,2108,state->reply.versionCount.c_str());
 auto list=GetDlgItem(window,2103);auto selected=SelectedRow(window);SendMessageW(list,WM_SETREDRAW,FALSE,0);ListView_DeleteAllItems(list);
 std::vector<size_t> order(state->reply.rows.size());std::iota(order.begin(),order.end(),0);
 std::stable_sort(order.begin(),order.end(),[&](size_t a,size_t b){
  auto first=state->reply.rows[a].modifiedTicks,second=state->reply.rows[b].modifiedTicks;
  return state->oldestFirst?first<second:first>second;
 });
 for(size_t i=0;i<order.size();++i){
  auto index=order[i];const auto& row=state->reply.rows[index];auto name=row.name;auto modified=row.modified;
  if(row.flags&1)modified+=L" "+fh::Text(TextId::Shell_LatestTag);else if(row.flags&2)modified+=L" "+fh::Text(TextId::Shell_OldestTag);
  LVITEMW item{};item.mask=LVIF_TEXT|LVIF_PARAM;item.iItem=static_cast<int>(i);item.pszText=name.data();item.lParam=static_cast<LPARAM>(index);
  fh::Check(ListView_InsertItem(list,&item)>=0);ListView_SetItemText(list,item.iItem,1,modified.data());
  if((selected>=0&&static_cast<size_t>(selected)==index)||(selected<0&&index==0))ListView_SetItemState(list,item.iItem,LVIS_SELECTED|LVIS_FOCUSED,LVIS_SELECTED|LVIS_FOCUSED);
 }
 HDITEMW column{};column.mask=HDI_FORMAT;auto header=ListView_GetHeader(list);Header_GetItem(header,1,&column);
 column.fmt=(column.fmt&~(HDF_SORTUP|HDF_SORTDOWN))|(state->oldestFirst?HDF_SORTUP:HDF_SORTDOWN);Header_SetItem(header,1,&column);
 auto focused=ListView_GetNextItem(list,-1,LVNI_FOCUSED);if(focused>=0)ListView_EnsureVisible(list,focused,FALSE);
 SendMessageW(list,WM_SETREDRAW,TRUE,0);InvalidateRect(list,nullptr,TRUE);Actions(window,state);
 SetDlgItemTextW(window,2105,fh::Text(state->selected.kind?TextId::Shell_BrowseFolder:state->reply.rows.empty()?TextId::Shell_OpenApp:TextId::Shell_BrowseVersions).c_str());
 ShowWindow(GetDlgItem(window,2103),state->selected.kind?SW_HIDE:SW_SHOW);
 ShowWindow(GetDlgItem(window,2108),state->selected.kind?SW_HIDE:SW_SHOW);
 ShowWindow(GetDlgItem(window,2104),state->selected.kind?SW_HIDE:SW_SHOW);ShowWindow(GetDlgItem(window,2106),state->selected.kind?SW_HIDE:SW_SHOW);
 SetDlgItemTextW(window,2107,fh::Text(state->selected.kind?TextId::Shell_FolderFooter:TextId::Shell_FileFooter).c_str());
 ShowWindow(GetDlgItem(window,2107),!state->selected.kind&&!state->reply.rows.empty()?SW_SHOW:SW_HIDE);
}
INT_PTR CALLBACK Dialog(HWND window,UINT message,WPARAM wp,LPARAM lp) noexcept {
 try {
  auto page=reinterpret_cast<Page*>(GetWindowLongPtrW(window,GWLP_USERDATA));
  if(message==WM_INITDIALOG){auto data=reinterpret_cast<PROPSHEETPAGEW*>(lp);page=reinterpret_cast<Page*>(data->lParam);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(page));
   {std::lock_guard lock(page->state->mutex);page->state->window=window;page->state->language=fh::UiLanguage();}
   auto list=GetDlgItem(window,2103);ListView_SetExtendedListViewStyle(list,LVS_EX_FULLROWSELECT|LVS_EX_DOUBLEBUFFER|LVS_EX_LABELTIP);
   RECT bounds{};GetClientRect(list,&bounds);auto width=bounds.right-GetSystemMetrics(SM_CXVSCROLL)-2;
   auto name=fh::Text(TextId::Shell_Name),modified=fh::Text(TextId::Shell_Modified);
   LVCOLUMNW column{};column.mask=LVCF_TEXT|LVCF_WIDTH|LVCF_SUBITEM;column.pszText=name.data();column.cx=width*38/100;ListView_InsertColumn(list,0,&column);
   column.pszText=modified.data();column.cx=width-column.cx;column.iSubItem=1;ListView_InsertColumn(list,1,&column);
   auto icon=LoadImageW(fh::module,MAKEINTRESOURCEW(1),IMAGE_ICON,MulDiv(32,static_cast<int>(GetDpiForWindow(window)),96),MulDiv(32,static_cast<int>(GetDpiForWindow(window)),96),LR_SHARED);
   SendDlgItemMessageW(window,2109,STM_SETICON,reinterpret_cast<WPARAM>(icon),0);
   if(page->state->selected.kind){RECT status{10,40,250,180};MapDialogRect(window,&status);SetWindowPos(GetDlgItem(window,2102),nullptr,status.left,status.top,status.right-status.left,status.bottom-status.top,SWP_NOZORDER|SWP_NOACTIVATE);}
   Render(window,page->state);StartWorker(page->state);return TRUE;}
  if(!page)return FALSE;auto state=page->state;
  if(message==Updated){if(wp==state->generation && !state->closed)Render(window,state);return TRUE;}
  if(message==WM_DESTROY){state->closed=true;{std::lock_guard lock(state->mutex);state->window=nullptr;}SetEvent(state->cancel.value);SetWindowLongPtrW(window,GWLP_USERDATA,0);return FALSE;}
  if(message==WM_NOTIFY){
   auto change=reinterpret_cast<NMHDR*>(lp);if(change->idFrom==2103){
    if(state->rendering)return TRUE;
    if(change->code==LVN_COLUMNCLICK){if(reinterpret_cast<NMLISTVIEW*>(lp)->iSubItem==1){state->oldestFirst=!state->oldestFirst;Render(window,state);}return TRUE;}
    if(change->code==LVN_ITEMCHANGED){std::lock_guard lock(state->mutex);Actions(window,state);return TRUE;}
   }
  }
  if(message==WM_COMMAND){
   auto command=LOWORD(wp);
   if(command==2105){auto result=fh::Browse(state->selected);if(FAILED(result))MessageBoxW(window,fh::Text(TextId::Shell_AppUnavailable).c_str(),L"Fluff-History",MB_OK|MB_ICONINFORMATION);return TRUE;}
   if(command==2104||command==2106){
    {std::lock_guard lock(state->mutex);if(state->busy)return TRUE;}
    if(command==2104 && MessageBoxW(window,fh::Text(TextId::Shell_RestoreConfirmation).c_str(),fh::Text(TextId::Shell_RestoreTitle).c_str(),MB_OKCANCEL|MB_ICONWARNING|MB_DEFBUTTON2)!=IDOK)return TRUE;
    auto selected=SelectedRow(window);
    {std::lock_guard lock(state->mutex);if(!state->reply.restore||selected<0||static_cast<size_t>(selected)>=state->reply.rows.size())return TRUE;state->version=state->reply.rows[static_cast<size_t>(selected)].token;
     state->busy=true;state->status=command==2104?TextId::Shell_Restoring:TextId::Shell_Previewing;state->next=command==2104?2:3;}
    Render(window,state);SetEvent(state->command.value);return TRUE;
   }
  }
 }catch(...){try{SetDlgItemTextW(window,2102,fh::Text(TextId::Shell_UnavailableContinue).c_str());}catch(...){}EnableWindow(GetDlgItem(window,2104),FALSE);EnableWindow(GetDlgItem(window,2106),FALSE);}
 return FALSE;
}
UINT CALLBACK PageCallback(HWND,UINT message,LPPROPSHEETPAGEW data) noexcept {
 if(message==PSPCB_RELEASE){auto page=reinterpret_cast<Page*>(data->lParam);if(page){page->state->closed=true;SetEvent(page->state->cancel.value);delete page;}--fh::objects;}return 1;
}
class HistoryPropertySheet final:public IShellExtInit,public IShellPropSheetExt {
 std::atomic<ULONG> refs{1};fh::Selection selected;bool initialized{};
public:
 HistoryPropertySheet(){++fh::objects;}~HistoryPropertySheet(){--fh::objects;}
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void**p) override {if(!p)return E_POINTER;*p=nullptr;if(iid==IID_IUnknown||iid==IID_IShellExtInit)*p=static_cast<IShellExtInit*>(this);else if(iid==IID_IShellPropSheetExt)*p=static_cast<IShellPropSheetExt*>(this);if(!*p)return E_NOINTERFACE;AddRef();return S_OK;}
 ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
 ULONG STDMETHODCALLTYPE Release() override{auto n=--refs;if(!n)delete this;return n;}
 HRESULT STDMETHODCALLTYPE Initialize(PCIDLIST_ABSOLUTE,IDataObject* data,HKEY) override{return fh::Boundary([&](){initialized=false;if(!data)return E_INVALIDARG;ComPtr<IShellItemArray> items;auto hr=SHCreateShellItemArrayFromDataObject(data,IID_PPV_ARGS(&items));if(FAILED(hr))return hr;hr=fh::Selected(items.Get(),&selected);initialized=SUCCEEDED(hr);return hr;});}
 HRESULT STDMETHODCALLTYPE ReplacePage(UINT,LPFNSVADDPROPSHEETPAGE,LPARAM) override{return E_NOTIMPL;}
 HRESULT STDMETHODCALLTYPE AddPages(LPFNADDPROPSHEETPAGE add,LPARAM value) override{return fh::Boundary([&](){
  if(!initialized||!add)return E_INVALIDARG;auto page=std::make_unique<Page>();page->state=std::make_shared<State>(selected);
  INITCOMMONCONTROLSEX controls{sizeof(controls),ICC_LISTVIEW_CLASSES};if(!InitCommonControlsEx(&controls))return E_FAIL;
  PROPSHEETPAGEW data{};data.dwSize=sizeof(data);data.dwFlags=PSP_USETITLE|PSP_USECALLBACK;data.hInstance=fh::module;data.pszTemplate=MAKEINTRESOURCEW(201);data.pszTitle=L"Fluff-History";data.pfnDlgProc=Dialog;data.pfnCallback=PageCallback;data.lParam=reinterpret_cast<LPARAM>(page.get());
  ++fh::objects;auto handle=CreatePropertySheetPageW(&data);if(!handle){--fh::objects;return E_FAIL;}
  page.release();if(!add(handle,value))DestroyPropertySheetPage(handle);return S_OK;
 });}
};
}
STDAPI DllGetClassObject(REFCLSID cls,REFIID iid,void**p){return fh::ClassObject<HistoryPropertySheet>(cls,fh::PropertiesId,iid,p);}
STDAPI DllCanUnloadNow(){return fh::objects==0?S_OK:S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,void*){if(reason==DLL_PROCESS_ATTACH){fh::module=h;DisableThreadLibraryCalls(h);}return TRUE;}
