"""Test grids of the world edge test: name -> (entity id, position, velocity)"""

EDGE_GRIDS = {
    # The game's top speed for large grids without Relative Top Speed is 100 m/s
    "Edge 440 out": (777000133000001, (440_000, 0, 0), (100, 0, 0)),
    "Edge 490 out and sideways": (777000133000002, (0, 0, -490_000), (0, 30, -100)),
    "Edge 499 out": (777000133000003, (-499_000, 0, 0), (-100, 0, 0)),
    "Edge 505 still": (777000133000004, (0, 0, 505_000), (0, 0, 0)),
    "Edge 470 in": (777000133000005, (0, -470_000, 0), (0, 100, 0)),
}
