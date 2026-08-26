using Avalonia.Controls;
using Avalonia.Headless;
using System.Collections.ObjectModel;
using Diary.App.Controls;
using Diary.App.Models;
using Diary.GUIBase;
using Diary.GUIBase.ViewModels;

namespace Diary.AppTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class ViewLocatorCachingTests
    {
        private static HeadlessUnitTestSession _session = null!;

        [ClassInitialize]
        public static void Initialize(TestContext context)
        {
            _session = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        }

        [ClassCleanup]
        public static void Cleanup() => _session.Dispose();

        [TestMethod]
        public Task CacheableViewModelReusesViewPerInstance() => _session.Dispatch(() =>
        {
            var locator = new ViewLocator { CacheViews = true };
            var firstModel = new ViewModels.CacheableSampleViewModel();
            var secondModel = new ViewModels.CacheableSampleViewModel();

            var firstView = locator.Build(firstModel);
            var repeatedView = locator.Build(firstModel);
            var secondView = locator.Build(secondModel);

            Assert.AreSame(firstView, repeatedView);
            Assert.AreNotSame(firstView, secondView);
            Assert.AreSame(firstView, firstModel.View);
            Assert.AreSame(secondView, secondModel.View);
            Assert.AreEqual(1, firstModel.ShowCount);
            Assert.AreEqual(1, firstModel.HideCount);
            Assert.AreEqual(1, secondModel.ShowCount);
        }, CancellationToken.None);

        [TestMethod]
        public Task PreloadedViewIsUsedOnFirstActivationWithoutRunningLifecycleEarly() => _session.Dispatch(() =>
        {
            var locator = new ViewLocator { CacheViews = true };
            var model = new ViewModels.CacheableSampleViewModel();

            var preloaded = ViewLocator.PreloadCached(model);

            Assert.AreEqual(0, model.ShowCount);
            Assert.AreEqual(0, model.HideCount);
            Assert.AreSame(preloaded, locator.Build(model));
            Assert.AreEqual(1, model.ShowCount);
        }, CancellationToken.None);

        [TestMethod]
        public Task NonCacheableViewModelNeverEntersNavigationCache() => _session.Dispatch(() =>
        {
            var locator = new ViewLocator { CacheViews = true };
            var model = new ViewModels.TransientSampleViewModel();

            var first = locator.Build(model);
            var second = locator.Build(model);

            Assert.AreNotSame(first, second);
            Assert.ThrowsExactly<InvalidOperationException>(() => ViewLocator.PreloadCached(model));
        }, CancellationToken.None);

        [TestMethod]
        public Task NavigationHostKeepsPreloadedViewAttachedAndRunsLifecycleOnlyWhenSelected()
            => _session.Dispatch(async () =>
            {
                var firstModel = new ViewModels.CacheableSampleViewModel();
                var secondModel = new ViewModels.CacheableSampleViewModel();
                var host = new NavigationViewHost
                {
                    CurrentPage = firstModel,
                    Pages =
                    [
                        new NavigateInfo("第一页", "", firstModel),
                        new NavigateInfo("第二页", "", secondModel),
                    ],
                };
                var window = new Window { Content = host };
                window.Show();

                await host.PreloadNowAsync();

                var preloadedView = secondModel.View;
                Assert.IsNotNull(preloadedView);
                Assert.AreSame(host, preloadedView.Parent);
                Assert.IsFalse(preloadedView.IsVisible);
                Assert.AreEqual(0, secondModel.ShowCount);

                host.CurrentPage = secondModel;

                Assert.AreSame(preloadedView, secondModel.View);
                Assert.IsTrue(preloadedView.IsVisible);
                Assert.AreEqual(1, firstModel.HideCount);
                Assert.AreEqual(1, secondModel.ShowCount);
                window.Close();
            }, CancellationToken.None);

        [TestMethod]
        public Task NavigationHostDoesNotHidePageSelectedWhilePreloadIsQueued()
            => _session.Dispatch(async () =>
            {
                var firstModel = new ViewModels.CacheableSampleViewModel();
                var secondModel = new ViewModels.CacheableSampleViewModel();
                var host = new NavigationViewHost
                {
                    CurrentPage = firstModel,
                    Pages =
                    [
                        new NavigateInfo("第一页", "", firstModel),
                        new NavigateInfo("第二页", "", secondModel),
                    ],
                };
                var window = new Window { Content = host };
                window.Show();

                var preloadTask = host.PreloadNowAsync();
                host.CurrentPage = secondModel;
                await preloadTask;

                Assert.IsNotNull(secondModel.View);
                Assert.IsTrue(secondModel.View.IsVisible);
                Assert.AreEqual(1.0, secondModel.View.Opacity);
                Assert.IsTrue(secondModel.View.IsHitTestVisible);
                window.Close();
            }, CancellationToken.None);

        [TestMethod]
        public Task NavigationHostAddsRemovesAndRecreatesDynamicTrackerViews()
            => _session.Dispatch(async () =>
            {
                var fixedModel = new ViewModels.CacheableSampleViewModel();
                var firstTrackerModel = new ViewModels.CacheableSampleViewModel();
                var replacementTrackerModel = new ViewModels.CacheableSampleViewModel();
                var pages = new ObservableCollection<NavigateInfo>
                {
                    new("日记", "", fixedModel),
                };
                var host = new NavigationViewHost
                {
                    CurrentPage = fixedModel,
                    Pages = pages,
                };
                var window = new Window { Content = host };
                window.Show();

                pages.Add(new NavigateInfo("Redmine", "", firstTrackerModel));
                await host.PreloadNowAsync();

                var firstTrackerView = firstTrackerModel.View;
                Assert.IsNotNull(firstTrackerView);
                Assert.AreSame(host, firstTrackerView.Parent);
                Assert.AreEqual(2, host.Children.Count);

                host.CurrentPage = firstTrackerModel;
                pages.RemoveAt(1);
                Assert.AreSame(host, firstTrackerView.Parent, "当前动态页应保留到主窗口完成页面回退。");

                host.CurrentPage = fixedModel;
                Assert.IsNull(firstTrackerView.Parent);
                Assert.AreEqual(1, host.Children.Count);
                Assert.AreEqual(1, firstTrackerModel.HideCount);

                pages.Add(new NavigateInfo("Redmine", "", replacementTrackerModel));
                await host.PreloadNowAsync();

                Assert.IsNotNull(replacementTrackerModel.View);
                Assert.AreNotSame(firstTrackerView, replacementTrackerModel.View);
                Assert.AreSame(host, replacementTrackerModel.View.Parent);
                Assert.AreEqual(2, host.Children.Count);
                window.Close();
            }, CancellationToken.None);

        [TestMethod]
        public void ViewCacheabilityDefaultsToDisabledAndWorkEditorsOptOut()
        {
            var cacheabilityGetter = typeof(ViewModelBase)
                .GetProperty(nameof(ViewModelBase.IsViewCacheable))!
                .GetMethod!;
            var workEditor = (Diary.App.ViewModels.WorkEditorViewModel)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(Diary.App.ViewModels.WorkEditorViewModel));

            Assert.IsFalse(typeof(ViewModelBase).IsAbstract);
            Assert.IsFalse(cacheabilityGetter.IsAbstract);
            Assert.IsTrue(cacheabilityGetter.IsVirtual);
            Assert.IsFalse(new ViewModelBase().IsViewCacheable);
            Assert.IsFalse(workEditor.IsViewCacheable);
        }

        private sealed class TestApplication : TestBaseApplication;
    }
}

namespace Diary.AppTests.ViewModels
{
    public sealed class CacheableSampleViewModel : ViewModelBase
    {
        public override bool IsViewCacheable => true;
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }

        public override void OnShow() => ShowCount++;
        public override void OnHide() => HideCount++;
    }

    public sealed class TransientSampleViewModel : ViewModelBase
    {
    }
}

namespace Diary.AppTests.Views
{
    public sealed class CacheableSampleView : UserControl
    {
    }

    public sealed class TransientSampleView : UserControl
    {
    }
}
