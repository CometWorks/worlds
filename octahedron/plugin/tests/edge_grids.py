"""Test grids of the world edge test: name -> (entity id, position, velocity)"""

EDGE_GRIDS = {
    # The game's top speed for large grids without Relative Top Speed is 100 m/s
    # Enters the braking zone from outside
    "Edge 445 out": (777000133000001, (445_000, 0, 0), (100, 0, 0)),
    # Reaches the speed limit and brakes to a stop at the edge within minutes
    "Edge 480 out": (777000133000006, (339_411, 0, 339_411), (70.71, 0, 70.71)),
    "Edge 490 out and sideways": (777000133000002, (0, 0, -490_000), (0, 30, -100)),
    "Edge 499 out": (777000133000003, (-499_000, 0, 0), (-100, 0, 0)),
    "Edge 505 still": (777000133000004, (0, 0, 505_000), (0, 0, 0)),
    "Edge 470 in": (777000133000005, (0, -470_000, 0), (0, 100, 0)),
}
