using Shiny.Feature;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Core {
    public sealed class FeatureCollection<TFeature>
        where TFeature : class, IServerFeature
    {
        private readonly List<TFeature> m_All = new();
        private readonly Dictionary<Type, TFeature> m_ByType = new();

        private readonly List<IPreTickable> m_PreTickables = new();
        private readonly List<ITickable> m_Tickables = new();
        private readonly List<IPostTickable> m_PostTickables = new();

        public IReadOnlyList<TFeature> All => m_All;

        public void Add(TFeature feature) {
            ArgumentNullException.ThrowIfNull(feature);

            var featureType = feature.GetType();
            if (m_ByType.ContainsKey(featureType)) {
                throw new InvalidOperationException($"Feature already added: {featureType.FullName}");
            }

            m_All.Add(feature);
            m_ByType[featureType] = feature;

            if (feature is IPreTickable pre) {
                m_PreTickables.Add(pre);
            }

            if (feature is ITickable tick) {
                m_Tickables.Add(tick);
            }

            if (feature is IPostTickable post) {
                m_PostTickables.Add(post);
            }
        }

        public T Get<T>() where T : class, TFeature {
            if (m_ByType.TryGetValue(typeof(T), out var feature)) {
                return (T)feature;
            }
            throw new InvalidOperationException($"Feature not found: {typeof(T).FullName}");
        }

        public T? TryGet<T>() where T : class, TFeature {
            if (m_ByType.TryGetValue(typeof(T), out var feature)) {
                return (T)feature;
            }
            return null;
        }

        public void InitAll(ServerContext context) {
            foreach (var feature in m_All) {
                feature.OnInit(context);
            }
        }

        public void StartAll() {
            foreach (var feature in m_All) {
                feature.OnStart();
            }
        }

        public void StopAllReverse() {
            for (int i = m_All.Count - 1; i >= 0; i--) {
                m_All[i].OnStop();
            }
        }

        public void RunPreTick() {
            foreach (var item in m_PreTickables) {
                item.OnPreTick();
            }
        }

        public void RunTick(in TickContext tick) {
            foreach (var item in m_Tickables) {
                item.OnTick(in tick);
            }
        }

        public void RunPostTick() {
            foreach (var item in m_PostTickables) {
                item.OnPostTick();
            }
        }
    }
}
