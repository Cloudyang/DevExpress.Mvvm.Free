using DevExpress.Mvvm.Native;
using DevExpress.Mvvm.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace DevExpress.Mvvm.ModuleInjection {
    public interface IRegion {
        string RegionName { get; }
        IEnumerable<object> ViewModels { get; }
        object SelectedViewModel { get; }
        string SelectedKey { get; }
        string GetKey(object viewModel);
        object GetViewModel(string key);

        LogicalSerializationMode LogicalSerializationMode { get; set; }
        VisualSerializationMode VisualSerializationMode { get; set; }
        void SetLogicalSerializationMode(string key, LogicalSerializationMode? mode);
        void SetVisualSerializationMode(string key, VisualSerializationMode? mode);
    }
}
namespace DevExpress.Mvvm.ModuleInjection.Native {
    public interface IRegionImplementation : IRegion {
        void RegisterUIRegion(IUIRegion region);
        void UnregisterUIRegion(IUIRegion region);
        IUIWindowRegion GetUIWindowRegion();
        void Inject(IModule module, object parameter);
        void Remove(string key);
        bool Contains(string key);
        void Clear();
        void Navigate(string key);
        void OnNavigation(string key, object vm);

        void GetInfo(out RegionInfo logicalInfo, out RegionVisualInfo visualInfo);
        void SetInfo(RegionInfo logicalInfo, RegionVisualInfo visualInfo);
        void ApplyInfo(bool inject, bool navigate);
        void SaveVisualState(object viewModel, string viewPart, string state);
        void GetSavedVisualState(object viewModel, string viewPart, out string state);
    }
    public interface IUIRegion {
        string RegionName { get; }
        IEnumerable<object> ViewModels { get; }
        object SelectedViewModel { get; set; }
        void Inject(object viewModel, Type viewType);
        void Remove(object viewModel);
        void Clear();
    }
    public interface IUIWindowRegion : IUIRegion {
        MessageBoxResult? Result { get; }
        void SetResult(MessageBoxResult result);
    }
}
namespace DevExpress.Mvvm.ModuleInjection.Native {
    class WeakReferenceManager<T> where T : class {
        readonly List<WeakReference> serviceReferences;

        public WeakReferenceManager() {
            serviceReferences = new List<WeakReference>();
        }
        public void Add(T service) {
            UpdateServiceReferences();
            AddCore(service);
        }
        public void Remove(T service) {
            UpdateServiceReferences();
            RemoveCore(service);
        }
        public bool Contains(T service) {
            return serviceReferences.FirstOrDefault(x => x.Target == service) != null;
        }
        public IEnumerable<T> Get() {
            UpdateServiceReferences();
            return serviceReferences.Select(x => x.Target).OfType<T>();
        }

        protected virtual void UpdateServiceReferences() {
            List<WeakReference> referencesToDelete = new List<WeakReference>();
            foreach(var reference in serviceReferences)
                if(!reference.IsAlive) referencesToDelete.Add(reference);
            foreach(var reference in referencesToDelete)
                serviceReferences.Remove(reference);
        }
        protected void AddCore(T service) {
            if(service == null) return;
            if(serviceReferences.FirstOrDefault(x => x.Target == service) != null) return;
            serviceReferences.Add(new WeakReference(service));
        }
        protected void RemoveCore(T service) {
            if(service == null) return;
            var serviceReference = serviceReferences.FirstOrDefault(x => x.Target == service);
            if(serviceReference == null) return;
            serviceReferences.Remove(serviceReference);
        }
    }
    public class Region : IRegionImplementation {
        readonly List<RegionItem> items;
        readonly WeakReferenceManager<IUIRegion> serviceManager;
        readonly IViewModelLocator viewModelLocator;
        readonly IViewLocator viewLocator;
        readonly IStateSerializer stateSerializer;
        string navigationKey;
        static string navigationKeyNull = "__region_navigationKeyNull__";

        public string RegionName { get; private set; }
        public IEnumerable<IUIRegion> UIRegions { get { return GetUIRegions(); } }
        public IEnumerable<object> ViewModels { get { return items.Where(x => x.ViewModel != null).Select(x => x.ViewModel); } }
        public string SelectedKey { get; private set; }
        public object SelectedViewModel { get { return GetViewModel(SelectedKey); } }

        public Region(string regionName, IModuleManagerImplementation owner, bool isTestingMode)
            : this(regionName, owner.ViewModelLocator, owner.ViewLocator, owner.ViewModelStateSerializer) {
            if(isTestingMode) {
                RegisterUIRegion(new TestUIRegion(regionName, owner));
            } else {
                var services = ServiceContainer.Default.GetServices<IUIRegion>().Where(x => x.RegionName == regionName);
                foreach(var service in services) RegisterUIRegion(service);
            }
        }
        Region(string regionName, IViewModelLocator viewModelLocator, IViewLocator viewLocator, IStateSerializer stateSerializer) {
            LogicalSerializationMode = LogicalSerializationMode.Enabled;
            this.RegionName = regionName;
            this.viewModelLocator = viewModelLocator;
            this.viewLocator = viewLocator;
            this.stateSerializer = stateSerializer;
            this.serviceManager = new WeakReferenceManager<IUIRegion>();
            this.items = new List<RegionItem>();
        }
        public void RegisterUIRegion(IUIRegion region) {
            if(serviceManager.Contains(region)) return;
            foreach(var item in items) item.Inject(region);
            serviceManager.Add(region);
            if(!TryNavigate() && SelectedKey != null)
                region.SelectedViewModel = SelectedViewModel;
        }
        public void UnregisterUIRegion(IUIRegion region) {
            if(!serviceManager.Contains(region)) return;
            serviceManager.Remove(region);
        }
        public IUIWindowRegion GetUIWindowRegion() {
            return UIRegions.OfType<IUIWindowRegion>().LastOrDefault();
        }
        public string GetKey(object viewModel) {
            if(viewModel == null) return null;
            var item = items.Where(x => x.ViewModel == viewModel).FirstOrDefault();
            return item != null ? item.Key : null;
        }
        public object GetViewModel(string key) {
            if(key == null) return null;
            var item = GetItem(key);
            return item != null ? item.ViewModel : null;
        }

        public void Inject(IModule module, object parameter) {
            var item = new RegionItem(viewModelLocator, viewLocator, stateSerializer, module, parameter);
            items.Add(item);
            DoForeachUIRegion(item.Inject);
            TryNavigate();
        }
        public void Remove(string key) {
            if(key == null) return;
            var item = GetItem(key);
            if(item == null) return;
            if(item.ViewModel != null)
                DoForeachUIRegion(x => x.Remove(item.ViewModel));
            items.Remove(item);
        }
        public bool Contains(string key) {
            return GetItem(key) != null;
        }
        public void Clear() {
            DoForeachUIRegion(x => x.Clear());
            items.Clear();
            navigationKey = null;
            SelectedKey = null;
        }
        public void Navigate(string key) {
            navigationKey = key == null ? navigationKeyNull : key;
            TryNavigate();
        }
        public void OnNavigation(string key, object vm) {
            SelectedKey = key;
            DoForeachUIRegion(x => x.SelectedViewModel = vm);
        }
        bool TryNavigate() {
            if(navigationKey == null || !GetUIRegions().Any()) return false;
            if(navigationKey == navigationKeyNull) {
                DoForeachUIRegion(x => x.SelectedViewModel = null);
                navigationKey = null;
                return true;
            }
            var item = GetItem(navigationKey);
            if(item == null || item.ViewModel == null) return false;
            DoForeachUIRegion(x => x.SelectedViewModel = item.ViewModel);
            navigationKey = null;
            return true;
        }

        IEnumerable<IUIRegion> GetUIRegions() {
            return serviceManager.Get().Where(x => x.RegionName == RegionName).ToArray();
        }
        void DoForeachUIRegion(Action<IUIRegion> action) {
            GetUIRegions().Reverse().ToList().ForEach(action);
        }
        RegionItem GetItem(string key) {
            if(key == null) return null;
            return items.Where(x => x.Key == key).FirstOrDefault();
        }
        RegionItem GetItem(object viewModel) {
            if(viewModel == null) return null;
            return items.Where(x => x.ViewModel == viewModel).FirstOrDefault();
        }

        #region Serialization
        LogicalSerializationMode logicalSerializationMode = LogicalSerializationMode.Enabled;
        VisualSerializationMode visualSerializationMode = VisualSerializationMode.PerViewType;
        public LogicalSerializationMode LogicalSerializationMode { get { return logicalSerializationMode; } set { logicalSerializationMode = value; } }
        public VisualSerializationMode VisualSerializationMode { get { return visualSerializationMode; } set { visualSerializationMode = value; } }
        RegionVisualInfo visualState;
        RegionVisualInfo VisualState {
            get { return visualState ?? (visualState = new RegionVisualInfo() { RegionName = RegionName }); }
            set {
                if(value == null) value = new RegionVisualInfo() { RegionName = RegionName };
                if(value.RegionName != RegionName) return;
                visualState = value;
            }
        }
        string restoreSelectedKey;
        readonly List<string> disabledSerialization = new List<string>();
        readonly Dictionary<string, LogicalSerializationMode> customLogicalSerializationMode = new Dictionary<string, LogicalSerializationMode>();
        readonly Dictionary<string, VisualSerializationMode> customVisualSerializationMode = new Dictionary<string, VisualSerializationMode>();

        public void SetLogicalSerializationMode(string key, LogicalSerializationMode? mode) {
            SetCustomSerializationMode(customLogicalSerializationMode, key, mode);
        }
        public void SetVisualSerializationMode(string key, VisualSerializationMode? mode) {
            SetCustomSerializationMode(customVisualSerializationMode, key, mode);
        }
        LogicalSerializationMode GetLogicalSerializationMode(string key) {
            return GetSerializationMode(customLogicalSerializationMode, key, LogicalSerializationMode);
        }
        VisualSerializationMode GetVisualSerializationMode(string key) {
            return GetSerializationMode(customVisualSerializationMode, key, VisualSerializationMode);
        }
        void SetCustomSerializationMode<T>(Dictionary<string, T> storage, string key, T? mode) where T : struct {
            if(mode == null) {
                if(storage.ContainsKey(key))
                    storage.Remove(key);
                return;
            }
            if(!storage.ContainsKey(key))
                storage.Add(key, mode.Value);
            else storage[key] = mode.Value;
        }
        T GetSerializationMode<T>(Dictionary<string, T> storage, string key, T globalValue) {
            if(storage.ContainsKey(key))
                return storage[key];
            return globalValue;
        }

        public void GetInfo(out RegionInfo logicalInfo, out RegionVisualInfo visualInfo) {
            logicalInfo = new RegionInfo() { RegionName = RegionName };
            if(LogicalSerializationMode == LogicalSerializationMode.Enabled)
                logicalInfo.SelectedViewModelKey = SelectedKey;
            foreach(var item in items) {
                if(GetLogicalSerializationMode(item.Key) == LogicalSerializationMode.Disabled) continue;
                var itemInfo = item.GetLogicalInfo();
                if(itemInfo != null) logicalInfo.Items.Add(itemInfo);
            }

            UpdateVisualState();
            visualInfo = VisualState;
        }
        public void SetInfo(RegionInfo logicalInfo, RegionVisualInfo visualInfo) {
            VisualState = visualInfo;
            if(logicalInfo == null) return;
            foreach(var itemInfo in logicalInfo.Items) {
                if(GetLogicalSerializationMode(itemInfo.Key) == LogicalSerializationMode.Disabled) continue;
                items.Add(new RegionItem(viewModelLocator, viewLocator, stateSerializer, itemInfo));
            }
            restoreSelectedKey = logicalInfo.SelectedViewModelKey;
        }
        public void ApplyInfo(bool inject, bool navigate) {
            if(items.Count == 0) return;
            if(inject) {
                foreach(var item in items)
                    DoForeachUIRegion(x => item.Inject(x));
            }
            if(navigate) {
                Navigate(restoreSelectedKey);
                restoreSelectedKey = null;
            }
        }

        public void SaveVisualState(object viewModel, string viewPart, string state) {
            var item = GetItem(viewModel);
            if(item == null) return;
            var info = GetVisualInfo(item.Key, item.GetViewName(), viewPart, true);
            if(info == null) return;
            info.State = new SerializableState(state);
        }
        public void GetSavedVisualState(object viewModel, string viewPart, out string state) {
            state = null;
            var item = GetItem(viewModel);
            if(item == null) return;
            var info = GetVisualInfo(item.Key, item.GetViewName(), viewPart, false);
            if(info == null || info.State == null) return;
            state = info.State.State;
        }
        RegionItemVisualInfo GetVisualInfo(string key, string viewName, string viewPart, bool createIfNotExist) {
            RegionItemVisualInfo res = null;
            if(GetVisualSerializationMode(key) == VisualSerializationMode.Disabled)
                return null;
            if(GetVisualSerializationMode(key) == VisualSerializationMode.PerKey)
                res = VisualState.Items.FirstOrDefault(x => x.Key == key && x.ViewName == viewName && x.ViewPart == viewPart);
            else if(GetVisualSerializationMode(key) == VisualSerializationMode.PerViewType) {
                res = VisualState.Items.FirstOrDefault(x => x.ViewName == viewName && x.ViewPart == viewPart);
                if(res != null) res.Key = null;
            }
            if(res == null && createIfNotExist) {
                res = new RegionItemVisualInfo() { ViewName = viewName, ViewPart = viewPart };
                if(GetVisualSerializationMode(key) == VisualSerializationMode.PerKey)
                    res.Key = key;
                VisualState.Items.Add(res);
            }
            return res;
        }
        void UpdateVisualState() {
            ViewModels.SelectMany(x => VisualStateServiceHelper.GetServices(x, false, true))
                .ToList().ForEach(x => x.EnforceSaveState());
        }
        #endregion
        #region RegionItem
        class RegionItem {
            public string Key { get; private set; }
            public object ViewModel { get { return viewModelRef != null ? viewModelRef.Target : null; } }
            Func<object> Factory { get; set; }
            string ViewModelName { get; set; }
            string ViewName { get; set; }
            Type ViewType { get; set; }
            object ViewModelState { get; set; }
            object Parameter { get; set; }

            WeakReference viewModelRef;
            readonly IViewModelLocator viewModelLocator;
            readonly IViewLocator viewLocator;
            readonly IStateSerializer stateSerializer;
            public RegionItem(IViewModelLocator viewModelLocator, IViewLocator viewLocator, IStateSerializer stateSerializer, IModule module, object parameter)
                : this(viewModelLocator, viewLocator, stateSerializer, module.Key, module.ViewModelFactory, module.ViewModelName, module.ViewName, module.ViewType, parameter) { }
            public RegionItem(IViewModelLocator viewModelLocator, IViewLocator viewLocator, IStateSerializer stateSerializer, RegionItemInfo info)
                : this(viewModelLocator, viewLocator, stateSerializer, info.Key, null, info.ViewModelName, info.ViewName, null, null) {
                SetViewModelState(info.ViewModelState.With(x => x.State), info.ViewModelStateType);
            }
            RegionItem(IViewModelLocator viewModelLocator, IViewLocator viewLocator, IStateSerializer stateSerializer,
                string key, Func<object> factory, string viewModelName, string viewName, Type viewType, object parameter) {
                this.viewModelLocator = viewModelLocator;
                this.viewLocator = viewLocator;
                this.stateSerializer = stateSerializer;

                Key = key;
                Factory = factory;
                if(Factory == null)
                    ViewModelName = viewModelName;
                ViewType = viewType;
                if(ViewType == null)
                    ViewName = viewName;
                Parameter = parameter;
            }

            public RegionItemInfo GetLogicalInfo() {
                if(ViewModel == null) return null;
                RegionItemInfo res = new RegionItemInfo() {
                    Key = Key, ViewModelName = ViewModelName, ViewName = ViewName,
                };
                string state, stateType;
                GetViewModelState(out state, out stateType);
                res.ViewModelStateType = stateType;
                res.ViewModelState = new SerializableState(state);
                return res;
            }
            void GetViewModelState(out string state, out string stateType) {
                state = null;
                stateType = null;
                if(ViewModel == null) return;
                object vmState = ISupportStateHelper.GetState(ViewModel);
                if(vmState == null) return;
                state = stateSerializer.SerializeState(vmState, vmState.GetType());
                stateType = vmState.GetType().AssemblyQualifiedName;
            }
            void SetViewModelState(string state, string stateType) {
                if(string.IsNullOrEmpty(state) || string.IsNullOrEmpty(stateType)) return;
                object vmState = stateSerializer.DeserializeState(state, Type.GetType(stateType));
                ViewModelState = vmState;
                UpdateViewModelState();
            }
            void UpdateViewModelState() {
                if(ViewModelState == null || ViewModel == null) return;
                ISupportStateHelper.RestoreState(ViewModel, ViewModelState);
            }

            public string GetViewName() {
                InitViewName();
                return ViewName;
            }
            public void Inject(IUIRegion service) {
                if(ViewModel == null) {
                    Init();
                    UpdateViewModelState();
                }
                service.Inject(ViewModel, ViewType);
            }
            void Init() {
                object vm = null;
                if(Factory != null) vm = Factory();
                else if(ViewModelName != null) vm = viewModelLocator.ResolveViewModel(ViewModelName);
                if(vm == null)
                    ModuleInjectionException.NullVM();
                viewModelRef = new WeakReference(vm);
                InitParameter();
                if(string.IsNullOrEmpty(ViewModelName))
                    ViewModelName = viewModelLocator.GetViewModelTypeName(vm.GetType());
                InitViewType();
                InitViewName();
            }
            void InitParameter() {
                if(Parameter == null) return;
                Verifier.VerifyViewModelISupportParameter(ViewModel);
                ((ISupportParameter)ViewModel).Parameter = Parameter;
                Parameter = null;
            }
            void InitViewType() {
                if(ViewType != null || string.IsNullOrEmpty(ViewName))
                    return;
                ViewType = viewLocator.ResolveViewType(ViewName);
            }
            void InitViewName() {
                if(!string.IsNullOrEmpty(ViewName)) return;
                if(ViewType == null) {
                    ViewName = null;
                    return;
                }
                ViewName = viewLocator.GetViewTypeName(ViewType);
            }

        }
        #endregion
        #region TestUIRegion
        class TestUIRegion : IUIRegion, IUIWindowRegion {
            readonly IModuleManagerImplementation owner;
            readonly ObservableCollection<object> viewModels;
            object selectedViewModel;
            public string RegionName { get; private set; }
            public TestUIRegion(string regionName, IModuleManagerImplementation owner) {
                this.owner = owner;
                RegionName = regionName;
                viewModels = new ObservableCollection<object>();
            }
            void OnSelectedViewModelChanged(object oldValue, object newValue) {
                owner.OnNavigation(RegionName,
                    new NavigationEventArgs(RegionName, oldValue, newValue,
                    owner.GetRegion(RegionName).GetKey(oldValue), owner.GetRegion(RegionName).GetKey(newValue)));
            }

            #region IModuleInjectionService
            IEnumerable<object> IUIRegion.ViewModels { get { return viewModels; } }
            object IUIRegion.SelectedViewModel {
                get { return selectedViewModel; }
                set {
                    if(selectedViewModel == value) return;
                    var oldValue = selectedViewModel;
                    selectedViewModel = value;
                    OnSelectedViewModelChanged(oldValue, selectedViewModel);
                }
            }
            void IUIRegion.Inject(object viewModel, Type viewType) {
                if(viewModel == null) return;
                viewModels.Add(viewModel);
            }
            void IUIRegion.Remove(object viewModel) {
                if(!viewModels.Contains(viewModel)) return;
                viewModels.Remove(viewModel);
            }
            void IUIRegion.Clear() {
                viewModels.Clear();
            }
            #endregion
            #region IModuleInjectionWindowService
            MessageBoxResult? setResult;
            MessageBoxResult? IUIWindowRegion.Result { get { return setResult; } }
            void IUIWindowRegion.SetResult(MessageBoxResult result) {
                setResult = result;
            }
            #endregion
        }
        #endregion
    }
}