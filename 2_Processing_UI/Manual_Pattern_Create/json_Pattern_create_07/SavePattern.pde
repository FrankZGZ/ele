class SavePattern{
  
  void savepattern() {
    
    outer_inner_Array = new JSONArray();   //json file write
    innerObject = new JSONObject(); //json file write
    DataArray = new JSONArray();
    most_innerObject = new JSONObject();
  
    for (int i = 0; i < Elecs_Data.length; i++) {
      DataArray.setInt(i, Elecs_Data[i]); // Set data in the JSONArray
    }
    
    innerObject.setJSONArray("pattern", DataArray);
    most_innerObject.setInt("ON", ON_time);
    most_innerObject.setInt("OFF", OFF_time);
    innerObject.setJSONObject("delay", most_innerObject);
    
    if (visualizationValue == 1){
      json.setBoolean("Simulation", true);
      }
    else{
      json.setBoolean("Simulation", false);
      }
    
    if (executionValue == 1){
      json.setBoolean("Execution", true);
      }
    else{
      json.setBoolean("Execution", false);
      }
  
  }

}
