// Compile the actual page implementation into a native-only isolated test host. Its internal
// activation seam substitutes bounded fake pipe peers; the shipped DLL has no diagnostic IPC.
#include "PropertySheet.cpp"
#include <thread>
#include <functional>
#include <iostream>
#include <algorithm>

namespace {
fh::Handle stop;
std::vector<std::thread> servers;
std::atomic<int> mode{},connected{};
HRESULT TestActivate(const std::wstring& arguments){
 return fh::Boundary([&](){auto token=arguments.substr(arguments.size()-32);fh::Check(fh::Token(token));auto name=L"\\\\.\\pipe\\FluffHistory.Shell.v1."+token;
  auto pipe=CreateNamedPipeW(name.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED,PIPE_TYPE_BYTE|PIPE_REJECT_REMOTE_CLIENTS,1,4096,4096,0,nullptr);fh::Check(pipe!=INVALID_HANDLE_VALUE);auto behavior=mode.load();
  servers.emplace_back([pipe,behavior](){fh::Handle server(pipe);try{
   fh::Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));OVERLAPPED op{};op.hEvent=event.value;BOOL result=ConnectNamedPipe(pipe,&op);auto error=result?ERROR_SUCCESS:GetLastError();
   if(!result&&error==ERROR_IO_PENDING){HANDLE waits[]={stop.value,event.value};if(WaitForMultipleObjects(2,waits,FALSE,3000)!=WAIT_OBJECT_0+1){CancelIoEx(pipe,&op);DWORD ignored{};GetOverlappedResult(pipe,&op,&ignored,TRUE);return;}}
   else fh::Check(result||error==ERROR_PIPE_CONNECTED);
   ReadFrame(pipe,stop.value,3000);++connected;
   if(behavior==3){WaitForSingleObject(stop.value,10000);return;}
   if(behavior==2)return; // Abrupt peer loss while the page awaits its reply.
   fh::Writer reply;reply.u32(behavior==1?99u:2u);reply.text(L"",32);reply.text(L"Not backed up by Fluff-History.",4096);reply.text(L"",32760);reply.u32(0);reply.u32(0);
   WriteFrame(pipe,stop.value,reply.frame(101));
   WaitForSingleObject(stop.value,10000);
  }catch(...) {}});return S_OK;
 });
}
bool PumpUntil(const std::function<bool()>& done,DWORD milliseconds=5000){
 auto end=GetTickCount64()+milliseconds;
 do{MSG message;while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}if(done())return true;MsgWaitForMultipleObjects(0,nullptr,FALSE,10,QS_ALLINPUT);}while(GetTickCount64()<end);return done();
}
HWND ShowPage(const std::shared_ptr<State>& state){
 auto page=new Page{state};PROPSHEETPAGEW data{};data.dwSize=sizeof(data);data.dwFlags=PSP_USETITLE|PSP_USECALLBACK;data.hInstance=fh::module;data.pszTemplate=MAKEINTRESOURCEW(201);data.pszTitle=L"Fluff-History";data.pfnDlgProc=Dialog;data.pfnCallback=PageCallback;data.lParam=reinterpret_cast<LPARAM>(page);++fh::objects;
 auto handle=CreatePropertySheetPageW(&data);fh::Check(handle!=nullptr);PROPSHEETHEADERW header{};header.dwSize=sizeof(header);header.dwFlags=PSH_MODELESS|PSH_NOAPPLYNOW;header.hInstance=fh::module;header.pszCaption=L"Native Shell fault validation";header.nPages=1;header.phpage=&handle;
 auto window=reinterpret_cast<HWND>(PropertySheetW(&header));fh::Check(window && window!=reinterpret_cast<HWND>(-1));return window;
}
void Finish(HWND sheet){
 DestroyWindow(sheet);fh::Check(PumpUntil([](){return fh::objects==0;}));SetEvent(stop.value);
 for(auto& thread:servers)thread.join();servers.clear();CloseHandle(stop.value);stop.value=INVALID_HANDLE_VALUE;
}
std::atomic<int> lifetimeActivations{};
HRESULT NoBrokerActivation(const std::wstring&){++lifetimeActivations;return E_FAIL;}
// A single occupied worker makes "submitted but not started" deterministic. The timeout
// also makes an accidental Shell-thread wait fail instead of hanging the regression host.
struct OccupiedPool {
 fh::Handle entered{CreateEventW(nullptr,TRUE,FALSE,nullptr)},release{CreateEventW(nullptr,TRUE,FALSE,nullptr)},left{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
 PTP_POOL pool{};PTP_WORK blocker{};TP_CALLBACK_ENVIRON environment{};
 static void CALLBACK Block(PTP_CALLBACK_INSTANCE,void* context,PTP_WORK){
  auto self=static_cast<OccupiedPool*>(context);SetEvent(self->entered.value);
  WaitForSingleObject(self->release.value,10000);SetEvent(self->left.value);
 }
 OccupiedPool(){
  fh::Check(entered.valid()&&release.valid()&&left.valid());
  pool=CreateThreadpool(nullptr);fh::Check(pool!=nullptr);
  InitializeThreadpoolEnvironment(&environment);SetThreadpoolCallbackPool(&environment,pool);
  try{
   SetThreadpoolThreadMaximum(pool,1);fh::Check(SetThreadpoolThreadMinimum(pool,1)!=FALSE);
   blocker=CreateThreadpoolWork(Block,this,&environment);fh::Check(blocker!=nullptr);SubmitThreadpoolWork(blocker);
   fh::Check(WaitForSingleObject(entered.value,5000)==WAIT_OBJECT_0);
  }catch(...){Close();throw;}
 }
 void Close() noexcept {
  SetEvent(release.value);
  if(blocker){WaitForThreadpoolWorkCallbacks(blocker,FALSE);CloseThreadpoolWork(blocker);blocker=nullptr;}
  DestroyThreadpoolEnvironment(&environment);if(pool){CloseThreadpool(pool);pool=nullptr;}
 }
 ~OccupiedPool(){Close();}
 OccupiedPool(const OccupiedPool&)=delete;OccupiedPool& operator=(const OccupiedPool&)=delete;
};
bool AllExpired(const std::vector<std::weak_ptr<State>>& states){return std::all_of(states.begin(),states.end(),[](const auto& state){return state.expired();});}
void WorkerLifetimeStress(){
 constexpr int rounds=16,batch=32,rapid=128;
 for(int round=0;round<rounds;++round){
  fh::Check(fh::objects==0);lifetimeActivations=0;OccupiedPool pool;
  // Positive control: explicit cancellation really can recall a queued callback in this setup.
  std::atomic<int> controlCalls{};
  auto control=CreateThreadpoolWork([](PTP_CALLBACK_INSTANCE,void* context,PTP_WORK){++*static_cast<std::atomic<int>*>(context);},&controlCalls,&pool.environment);
  fh::Check(control!=nullptr);SubmitThreadpoolWork(control);WaitForThreadpoolWorkCallbacks(control,TRUE);CloseThreadpoolWork(control);fh::Check(controlCalls==0);
  std::vector<std::weak_ptr<State>> states;states.reserve(batch);
  for(int i=0;i<batch;++i){
   auto state=std::make_shared<State>(fh::Selection{L"C:\\synthetic-not-opened.txt",0});state->activate=NoBrokerActivation;state->workerEnvironment=&pool.environment;
   states.emplace_back(state);auto sheet=ShowPage(state);DestroyWindow(sheet);
   fh::Check(state->closed && !IsWindow(sheet));state.reset();
  }
  // These are actual StartWorker submissions from actual page creation/closure, not a
  // duplicate queue implementation. Pending contexts must keep each state and count alive.
  fh::Check(WaitForSingleObject(pool.left.value,0)==WAIT_TIMEOUT);
  fh::Check(lifetimeActivations==0 && fh::objects==batch);
  fh::Check(std::none_of(states.begin(),states.end(),[](const auto& state){return state.expired();}));
  SetEvent(pool.release.value);
  fh::Check(PumpUntil([&](){return fh::objects==0&&AllExpired(states);},10000));
  fh::Check(lifetimeActivations==0); // Closed pages do not activate a broker when the callback finally runs.
 }
 // Also cover the opposite race: a default-pool callback can start before page destruction.
 std::vector<std::weak_ptr<State>> states;states.reserve(rapid);lifetimeActivations=0;
 for(int i=0;i<rapid;++i){
  auto state=std::make_shared<State>(fh::Selection{L"C:\\synthetic-not-opened.txt",0});state->activate=NoBrokerActivation;
  states.emplace_back(state);auto sheet=ShowPage(state);DestroyWindow(sheet);fh::Check(state->closed && !IsWindow(sheet));
 }
 fh::Check(PumpUntil([&](){return fh::objects==0&&AllExpired(states);},10000));
 std::cout<<"Worker lifetime: "<<rounds*batch<<" blocked-pool queued page closures + "<<rapid<<" default-pool rapid closures passed\n";
}
}
int wmain(){
 auto com=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);fh::module=GetModuleHandleW(nullptr);INITCOMMONCONTROLSEX controls{sizeof(controls),ICC_STANDARD_CLASSES|ICC_WIN95_CLASSES};InitCommonControlsEx(&controls);
 try{
  fh::Check(SUCCEEDED(com));
  for(int behavior:{1,2,3}){
   stop.value=CreateEventW(nullptr,TRUE,FALSE,nullptr);mode=behavior;connected=0;auto state=std::make_shared<State>(fh::Selection{L"C:\\synthetic-not-opened.txt",0});state->activate=TestActivate;auto sheet=ShowPage(state);
   fh::Check(PumpUntil([](){return connected>0;}));
   if(behavior==3){fh::Check(IsWindow(sheet)!=FALSE);Finish(sheet);fh::Check(!IsWindow(sheet));std::cout<<"Outstanding query close: passed\n";continue;}
   fh::Check(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy && !state->reply.restore && state->status==fh::TextId::Shell_UnavailableRefresh;}));
   DWORD_PTR ignored{};fh::Check(SendMessageTimeoutW(sheet,WM_NULL,0,0,SMTO_ABORTIFHUNG,500,&ignored)!=0);
   // A failed/malformed response must leave a working Refresh path on the same page.
   mode=0;{std::lock_guard lock(state->mutex);state->next=1;state->busy=true;}SetEvent(state->command.value);
   fh::Check(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy && state->reply.status==2;}));
   Finish(sheet);std::cout<<(behavior==1?"Malformed response + refresh: passed\n":"Broker disconnect + refresh: passed\n");
  }
  WorkerLifetimeStress();
  CoUninitialize();return 0;
 }catch(...){SetEvent(stop.value);for(auto& thread:servers)if(thread.joinable())thread.join();std::cerr<<"Native Shell fault check failed\n";if(SUCCEEDED(com))CoUninitialize();return 1;}
}
