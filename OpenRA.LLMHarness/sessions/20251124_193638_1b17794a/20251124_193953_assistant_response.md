**Strategic Advice**

Based on the current game state, it appears that both players are in the early stages of the game. The key strategic advice for Player1 (Jason) is to focus on building a strong economy by constructing multiple Refineries and Power Plants. This will allow Jason to collect Tiberium efficiently and generate power for his buildings.

Additionally, Jason should prioritize building Barracks and War Factories to train infantry and vehicle units respectively. This will give him a solid foundation for defending his base and expanding into the map.

It's also worth noting that cabal (Nod) has already built an Airfield and is producing Minigunners, which could be a threat if not addressed soon.

**Production Orders**

To achieve the above strategic goals, Player1 should construct the following buildings and train units:

```markdown
<orders>
# Building Construction

* Construct 2-3 additional Refineries near existing ones to maximize Tiberium collection.
	+ StartProduction (From:Refinery@22752,74752,0 TargetString:Refinery ExtraData:1 Queued:true)
	+ StartProduction (From:Refinery@22752,74752,0 TargetString:Refinery ExtraData:2 Queued:true)
* Construct 2-3 Power Plants to generate power for buildings.
	+ StartProduction (From:Construction Yard@19968,74752,0 TargetString:Power Plant ExtraData:1 Queued:true)
	+ StartProduction (From:Construction Yard@19968,74752,0 TargetString:Power Plant ExtraData:2 Queued:true)

# Unit Production

* Train 5-6 infantry units to defend the base.
	+ StartProduction (From:Barracks@22752,74752,0 TargetString:Infantry ExtraData:1 Queued:true)
	+ StartProduction (From:Barracks@22752,74752,0 TargetString:Infantry ExtraData:2 Queued:true)
* Train 2-3 vehicle units to expand into the map.
	+ StartProduction (From:War Factory@22752,74752,0 TargetString:Light Tank ExtraData:1 Queued:true)
	+ StartProduction (From:War Factory@22752,74752,0 TargetString:Medium Tank ExtraData:2 Queued:true)

</orders>
```

These production orders will help Player1 build a strong economy and military presence on the map.