﻿using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CombatExtended
{
    public class GenStep_Attrition : GenStep
    {
        private const float WORLD_SHELLDAMAGE = 0.015f;

        private const float MAP_SHELLMINDAMAGE = 180;
        private const float MAP_SHELLMAXDAMAGE = 800;
        private const float MAP_SHELLMINRADIUS = 6f;
        private const float MAP_SHELLMAXRADIUS = 20f;
        private const float MAP_BURNCHANCE = 0.3334f;

        private const int MAP_MINSITECOUNT = 6;
        private const int MAP_GENLIMITER = 50;

        private const int SHADOW_CARRYLIMIT = 5;
        private const float SITE_MINDIST = 2;

        private struct DamagedSite
        {
            public int radius;
            public float damage;
            public IntVec3 location;
        }

        private int siteCount;
        private Map map;
        private WorldObjects.HealthComp healthComp;

        private readonly List<DamagedSite> damagedSites = new List<DamagedSite>();

        public override int SeedPart => 1158116095;

        public GenStep_Attrition()
        {
        }

        public override void Generate(Map map, GenStepParams parms)
        {
            this.map = map;
            damagedSites.Clear();
            healthComp = this.map.Parent?.GetComponent<WorldObjects.HealthComp>() ?? null;
            if (healthComp != null && healthComp.Health < 0.98f)
            {
                try
                {
                    siteCount = (int)((1 - healthComp.Health) / WORLD_SHELLDAMAGE);
                    if (siteCount > 0)
                    {
                        siteCount = Mathf.Max(siteCount, MAP_MINSITECOUNT);
                        int m = siteCount * MAP_GENLIMITER;
                        int radius = GetRandomRadius();
                        bool burn = Rand.Chance(MAP_BURNCHANCE);
                        int dmg = GetRandomDamage();
                        while (m-- > 0 && damagedSites.Count < siteCount)
                        {
                            IntVec3 cell = new IntVec3((int)CE_Utility.RandomGaussian(1, map.Size.x - 1), 0, (int)CE_Utility.RandomGaussian(1, map.Size.z - 1));
                            RoofDef roof = cell.GetRoof(map);
                            if (roof != RoofDefOf.RoofRockThick && TryExplode(cell, dmg, radius) && !damagedSites.Any(d => d.location.DistanceToSquared(cell) < SITE_MINDIST))
                            {
                                DamagedSite site = new DamagedSite()
                                {
                                    location = cell,
                                    damage = dmg,
                                    radius = radius
                                };
                                burn = Rand.Chance(MAP_BURNCHANCE);
                                dmg = GetRandomDamage();
                                radius = GetRandomRadius();
                                damagedSites.Add(site);
                            }
                        }
                    }
                }
                catch (Exception er)
                {
                    Log.Error($"CE: GenStep_Attrition Failed with error {er}");
                }
            }
            this.map = null;
            this.damagedSites.Clear();
            this.healthComp = null;
        }

        private bool TryExplode(IntVec3 origin, int damageBase, int radius)
        {
            Action<IntVec3, int> processor = (cell, carry) =>
             {
                 if (!cell.InBounds(map) || cell.DistanceTo(origin) > radius)
                 {
                     return;
                 }
                 List<Thing> things = cell.GetThingList(map);
                 var filthMade = false;
                 var damageCell = (int)(damageBase * (SHADOW_CARRYLIMIT - carry) / SHADOW_CARRYLIMIT);
                 for (int i = 0; i < things.Count; i++)
                 {
                     Thing thing = things[i];
                     if (!thing.def.useHitPoints)
                     {
                         if (thing is Pawn p/* && Rand.Chance(0.4f)*/)//Copied from HealthUtility.DamageUntilDowned and modified
                        {
                             DamagePawn(p);
                         }
                         continue;
                     }
                     thing.hitPointsInt -= damageCell * (thing.IsPlant() ? 3 : 1);
                     if (thing.hitPointsInt > 0)
                     {
                         if (!filthMade && Rand.Chance(0.5f))
                         {
                             ScatterDebrisUtility.ScatterFilthAroundThing(thing, map, ThingDefOf.Filth_RubbleBuilding);
                             filthMade = true;
                         }
                         if (Rand.Chance(0.1f))
                         {
                             FireUtility.TryStartFireIn(cell, map, Rand.Range(0.5f, 1.5f));
                         }
                     }
                     else
                     {
                         thing.DeSpawn(DestroyMode.Vanish);
                         thing.Destroy(DestroyMode.Vanish);
                         if (thing.def.category == ThingCategory.Plant && (thing.def.plant?.IsTree ?? false))
                         {
                             Thing burntTree = ThingMaker.MakeThing(ThingDefOf.BurnedTree);
                             burntTree.positionInt = cell;
                             burntTree.SpawnSetup(map, false);
                             if (!filthMade && Rand.Chance(0.5f))
                             {
                                 ScatterDebrisUtility.ScatterFilthAroundThing(burntTree, map, ThingDefOf.Filth_Ash);
                                 filthMade = true;
                             }
                         }
                         if (thing.def.MakeFog)
                         {
                             map.fogGrid.Notify_FogBlockerRemoved(cell);
                         }
                         ThingDef filth = thing.def.filthLeaving ?? (Rand.Chance(0.5f) ? ThingDefOf.Filth_Ash : ThingDefOf.Filth_RubbleBuilding);
                         if (!filthMade && FilthMaker.TryMakeFilth(cell, map, filth, Rand.Range(1, 3), FilthSourceFlags.Any))
                         {
                             filthMade = true;
                         }
                     }
                 }
                 map.snowGrid.SetDepth(cell, 0);
                 map.roofGrid.SetRoof(cell, null);
                 if (Rand.Chance(0.33f) && map.terrainGrid.CanRemoveTopLayerAt(cell))
                 {
                     map.terrainGrid.RemoveTopLayer(cell, false);
                 }
             };
            processor(origin, 0);
            ShadowCastingUtility.CastWeighted(map, origin, processor, radius, SHADOW_CARRYLIMIT, out int count);
            return true;
        }
        void DamagePawn(Pawn pawn)
        {
            int hitsCount = Rand.Range(0, 8);
            for (int i = 0; i < hitsCount; i++)
            {
                if (pawn.Map == null)
                {
                    break;
                }
                var frag = GenSpawn.Spawn(Rand.Chance(0.3f) ? CE_ThingDefOf.Fragment_Large : CE_ThingDefOf.Fragment_Small, pawn.Position, pawn.Map) as ProjectileCE;
                frag.Impact(pawn);
            }
            //Flame dmg
            if (Rand.Chance(0.2f))
            {
                hitsCount = Rand.Range(3, 9);
                for (int i = 0; i < hitsCount; i++)
                {
                    BattleLogEntry_DamageTaken battleLogEntry_DamageTaken = new BattleLogEntry_DamageTaken(pawn, RulePackDefOf.DamageEvent_Fire, null);
                    Find.BattleLog.Add(battleLogEntry_DamageTaken);
                    int num = GenMath.RoundRandom(Mathf.Clamp(0.0125f + 0.0036f * Rand.Range(011f, 0.5f), 0.0125f, 0.05f) * 150f);
                    DamageInfo dinfo = new DamageInfo(DamageDefOf.Flame, (float)num, 0f, -1f, null, null, null, DamageInfo.SourceCategory.ThingOrUnknown, null, true, true);
                    dinfo.SetBodyRegion(BodyPartHeight.Undefined, BodyPartDepth.Outside);
                    pawn.TakeDamage(dinfo).AssociateWithLog(battleLogEntry_DamageTaken);
                    Apparel apparel;
                    if (pawn.apparel != null && pawn.apparel.WornApparel.TryRandomElement(out apparel))
                    {
                        apparel.TakeDamage(new DamageInfo(DamageDefOf.Flame, (float)num, 0f, -1f, null, null, null, DamageInfo.SourceCategory.ThingOrUnknown, null, true, true));
                        return;
                    }
                }
            }
            //Explosion dmg
            if (Rand.Chance(0.04f))
            {
                BattleLogEntry_DamageTaken battleLogEntry_DamageTaken = new BattleLogEntry_DamageTaken(pawn, CE_RulePackDefOf.DamageEvent_ShellingExplosion, null);
                Find.BattleLog.Add(battleLogEntry_DamageTaken);
                var num = Rand.Range(50f, 150f);
                DamageInfo dinfo = new DamageInfo(DamageDefOf.Bomb, (float)num, 0f, -1f, null, null, null, DamageInfo.SourceCategory.ThingOrUnknown, null, true, true);
                dinfo.SetBodyRegion(BodyPartHeight.Undefined, BodyPartDepth.Outside);
                pawn.TakeDamage(dinfo).AssociateWithLog(battleLogEntry_DamageTaken);
            }
            ResetVisualDamageEffects(pawn);
        }
        public static void ResetVisualDamageEffects(Pawn pawn)
        {
            var draw = pawn.drawer;
            (new Traverse(draw).Field("jitterer").GetValue() as JitterHandler)?.ProcessPostTickVisuals(10000);
            draw.renderer.graphics.flasher.lastDamageTick = -9999;
        }
        private int GetRandomDamage() => (int)CE_Utility.RandomGaussian(MAP_SHELLMINDAMAGE, MAP_SHELLMAXDAMAGE);
        private int GetRandomRadius() => Mathf.CeilToInt(CE_Utility.RandomGaussian(MAP_SHELLMINRADIUS, MAP_SHELLMAXRADIUS));
    }
}

