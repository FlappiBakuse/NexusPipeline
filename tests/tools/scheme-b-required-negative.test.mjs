import test from "node:test";

test("Scheme B migration probe fails Host / Required", () => {
  throw new Error("Scheme B migration probe: this failure must block Host / Required");
});
